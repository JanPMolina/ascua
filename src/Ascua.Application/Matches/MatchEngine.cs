using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Ascua.Application.Abstractions;
using Ascua.Application.Audit;
using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Microsoft.Extensions.Logging;

namespace Ascua.Application.Matches;

/// <summary>
/// Casos de uso del tiempo real: arrancar la partida, registrar órdenes, cerrar rondas, drafts,
/// abandonos y plazos (planteamiento/02-arquitectura.md, secciones 3 y 5). Es un singleton sin estado
/// propio: todo vive en Redis (caliente) y Postgres (durable), así que cualquier réplica puede atender
/// cualquier partida. Toda mutación pasa por <see cref="MutateAsync"/>: lock por partida, confirmación
/// en Postgres con versión (la restricción <c>UNIQUE(match_id, seq)</c> es la garantía final) y difusión.
/// </summary>
public sealed class MatchEngine(
    IMatchStore store,
    IMatchHotState hot,
    IMatchNotifier notifier,
    IActiveMatchRegistry activeMatches,
    IRandom rng,
    IClock clock,
    RealtimeOptions options,
    MatchMetrics metrics,
    ILogger<MatchEngine> logger) : IMatchLauncher
{
    /// <summary>Cuánto espera el lock una acción de un jugador (draft, abandono, cierre por órdenes completas).</summary>
    private static readonly TimeSpan CommandLockWait = TimeSpan.FromSeconds(3);

    private static readonly IReadOnlySet<PlayerId> NoDefaults = new HashSet<PlayerId>();

    public async Task<Guid> LaunchAsync(LaunchMatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = await store.FindByRoomAsync(request.RoomId, cancellationToken);
        if (existing is not null)
        {
            return existing.MatchId;
        }

        var names = await store.GetDisplayNamesAsync([.. request.Seats.Select(s => s.UserId)], cancellationToken);
        var participants = request.Seats
            .Select((seat, index) => new MatchParticipant(
                seat.UserId, index, seat.Hero, names.GetValueOrDefault(seat.UserId) ?? $"jugador{index + 1}"))
            .ToList();

        // El azar sale del CSPRNG del servidor (SecureRandom); la semilla nunca existe fuera de él.
        var (state, events) = MatchFactory.Create([.. participants.Select(p => new PlayerSetup(p.PlayerId, p.Hero))], rng, options.MaxRounds);
        var now = clock.UtcNow;
        var snapshot = new MatchSnapshot
        {
            MatchId = Guid.CreateVersion7(now),
            RoomId = request.RoomId,
            Status = MatchStatus.Drafting,
            Version = 1,
            State = state,
            Participants = participants,
            Deadline = now + options.DraftDuration,
            RoundDuration = options.RoundDuration,
            CreatedAt = now,
        };

        var stored = await store.CreateAsync(snapshot, events, cancellationToken);
        await hot.SaveAsync(stored, cancellationToken);
        if (stored.MatchId == snapshot.MatchId)
        {
            // Quien nunca entra cuenta como desconectado: al vencer el plazo se aplica el abandono.
            foreach (var participant in participants)
            {
                await hot.ScheduleGraceAsync(stored.MatchId, participant.UserId, now + options.JoinTimeout, cancellationToken);
            }

            logger.LogInformation(
                "Partida {MatchId} creada para la sala {RoomId} con {PlayerCount} jugadores (seq {Seq})",
                stored.MatchId, request.RoomId, participants.Count, state.LastSeq);
        }

        return stored.MatchId;
    }

    /// <summary>Instantánea caliente; si Redis no la tiene, se recupera de Postgres y se vuelve a guardar.</summary>
    public async Task<MatchSnapshot?> GetAsync(Guid matchId, CancellationToken cancellationToken)
    {
        var snapshot = await hot.GetAsync(matchId, cancellationToken);
        if (snapshot is not null)
        {
            return snapshot;
        }

        return await RepairFromStoreAsync(matchId, cancellationToken);
    }

    /// <summary>Partida y asiento del usuario, para leer su vista (también en partidas terminadas).</summary>
    public async Task<MatchResult<(MatchSnapshot Snapshot, MatchParticipant Participant)>> GetForPlayerAsync(
        Guid matchId, Guid userId, CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(matchId, cancellationToken);
        if (snapshot is null)
        {
            return new MatchError(RealtimeErrors.MatchNotFound, "La partida no existe.");
        }

        var participant = snapshot.FindParticipant(userId);
        if (participant is null)
        {
            return new MatchError(RealtimeErrors.NotParticipant, "No participas en esta partida.");
        }

        return MatchResult<(MatchSnapshot, MatchParticipant)>.Ok((snapshot, participant));
    }

    /// <summary>
    /// Asocia la conexión a la partida. Devuelve la conexión anterior del mismo usuario (hay que
    /// cerrarla con <c>SESSION_REPLACED</c>) y avisa a los demás si volvía de una desconexión.
    /// </summary>
    public async Task<MatchResult<(MatchSnapshot Snapshot, MatchParticipant Participant, string? ReplacedConnection)>> JoinAsync(
        Guid matchId, Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        var found = await GetForPlayerAsync(matchId, userId, cancellationToken);
        if (found.Error is { } error)
        {
            return error;
        }

        var (snapshot, participant) = found.Value;
        if (!snapshot.Status.IsActive())
        {
            return new MatchError(RealtimeErrors.MatchNotActive, "La partida ya no está en curso.") { Status = snapshot.Status };
        }

        var (previous, wasAway) = await hot.MarkConnectedAsync(matchId, userId, connectionId, cancellationToken);
        if (wasAway)
        {
            await notifier.PlayerConnectionChangedAsync(matchId, userId, ConnectionState.Connected, null, cancellationToken);
        }

        var replaced = previous is not null && previous != connectionId ? previous : null;
        return MatchResult<(MatchSnapshot, MatchParticipant, string?)>.Ok((snapshot, participant, replaced));
    }

    /// <summary>Se cayó la conexión vigente del usuario: empieza la gracia y la ronda ya no lo espera.</summary>
    public async Task DisconnectAsync(Guid matchId, Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(matchId, cancellationToken);
        if (snapshot is null || !snapshot.Status.IsActive())
        {
            return;
        }

        var graceDeadline = clock.UtcNow + options.ReconnectGrace;
        if (!await hot.MarkDisconnectedAsync(matchId, userId, connectionId, graceDeadline, cancellationToken))
        {
            return;
        }

        await notifier.PlayerConnectionChangedAsync(matchId, userId, ConnectionState.Disconnected, graceDeadline, cancellationToken);
        if (snapshot.Status == MatchStatus.InProgress)
        {
            ScheduleCloseCheck(matchId, snapshot.State.Round);
        }
    }

    /// <summary>
    /// Registra la orden secreta. Orden de validación del contrato: idempotencia → participante →
    /// partida en curso → héroe activo → ronda abierta → legalidad. Si ya decidieron todos, intenta cerrar.
    /// </summary>
    public async Task<MatchResult<(OrderAck Ack, bool Duplicate)>> SubmitOrderAsync(
        Guid matchId, Guid userId, int round, Guid clientActionId, Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        var previous = await hot.FindAckAsync(matchId, clientActionId, cancellationToken);
        if (previous is not null)
        {
            return previous.UserId == userId
                ? MatchResult<(OrderAck, bool)>.Ok((previous, true))
                : new MatchError(RealtimeErrors.InvalidPayload, "El clientActionId ya fue usado.") { Field = "clientActionId" };
        }

        var found = await GetForPlayerAsync(matchId, userId, cancellationToken);
        if (found.Error is { } error)
        {
            return error;
        }

        var (snapshot, participant) = found.Value;
        if (snapshot.Status != MatchStatus.InProgress)
        {
            return new MatchError(RealtimeErrors.MatchNotActive, "La partida no está en una ronda.") { Status = snapshot.Status };
        }

        var domainError = OrderValidator.Validate(snapshot.State, participant.PlayerId, round, order);
        if (domainError is not null)
        {
            return Map(domainError, snapshot.State.Round);
        }

        var ack = new OrderAck(matchId, round, clientActionId, userId, clock.UtcNow);
        var result = await hot.SubmitOrderAsync(ack, participant.PlayerId, order, cancellationToken);
        switch (result.Status)
        {
            case SubmitStatus.Duplicate:
                return MatchResult<(OrderAck, bool)>.Ok((result.Ack!, true));
            case SubmitStatus.AlreadySubmitted:
                return new MatchError(RealtimeErrors.OrderAlreadySubmitted, "Ya enviaste una orden en esta ronda; vale la primera.");
            case SubmitStatus.RoundClosed:
                return new MatchError(RealtimeErrors.StaleRound, $"La ronda {round} ya se cerró.") { CurrentRound = Math.Min(round + 1, options.MaxRounds) };
        }

        metrics.OrderAccepted();
        using (logger.BeginScope(new Dictionary<string, object> { ["MatchId"] = matchId, ["PlayerId"] = userId, ["Round"] = round }))
        {
            logger.LogDebug("Orden aceptada ({Decided}/{Expected})", result.DecidedCount, snapshot.ActivePlayers().Count);
        }

        var expected = snapshot.ActivePlayers().Count;
        await notifier.PlayerDecidedAsync(snapshot, userId, result.DecidedCount, expected, cancellationToken);
        if (result.DecidedCount >= expected || await AllDecidedAsync(snapshot, cancellationToken))
        {
            ScheduleCloseCheck(matchId, round);
        }

        return MatchResult<(OrderAck, bool)>.Ok((ack, false));
    }

    /// <summary>
    /// CloseRound: un solo cerrador por ronda. Sin <paramref name="deadlineExpired"/> solo cierra si
    /// ya decidieron todos los conectados; con él, completa las faltantes con GUARDAR.
    /// </summary>
    public async Task<bool> TryCloseRoundAsync(Guid matchId, int round, bool deadlineExpired, CancellationToken cancellationToken)
    {
        var mutation = await MutateAsync(
            matchId,
            deadlineExpired ? TimeSpan.Zero : CommandLockWait,
            async current =>
            {
                if (current.Status != MatchStatus.InProgress || current.State.Round != round)
                {
                    return null;
                }

                var now = clock.UtcNow;
                if (deadlineExpired ? current.Deadline > now : !await AllDecidedAsync(current, cancellationToken))
                {
                    return null;
                }

                var submitted = await hot.SealOrdersAsync(matchId, round, cancellationToken);
                var orders = new Dictionary<PlayerId, Order>();
                var defaults = new HashSet<PlayerId>();
                foreach (var player in current.ActivePlayers())
                {
                    if (submitted.TryGetValue(player, out var order) && OrderValidator.Validate(current.State, player, round, order) is null)
                    {
                        orders[player] = order;
                    }
                    else
                    {
                        orders[player] = Order.DefaultGuard;
                        defaults.Add(player);
                    }
                }

                var (next, events) = Domain(() => Resolver.Resolve(current.State, orders, rng));
                return Advance(current, next, events, now, roundResolved: true, defaults);
            },
            cancellationToken);

        return mutation.Status == MutationStatus.Changed;
    }

    /// <summary>Lo llama el temporizador de cada réplica cuando vence el plazo programado de la partida.</summary>
    public async Task ProcessDeadlineAsync(Guid matchId, CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(matchId, cancellationToken);
        if (snapshot is null || !snapshot.Status.IsActive() || snapshot.Deadline is null)
        {
            await hot.RemoveDeadlineAsync(matchId, cancellationToken);
            return;
        }

        var now = clock.UtcNow;
        if (snapshot.Deadline > now)
        {
            await hot.SaveAsync(snapshot, cancellationToken);
            return;
        }

        if (snapshot.Status == MatchStatus.InProgress)
        {
            await TryCloseRoundAsync(matchId, snapshot.State.Round, deadlineExpired: true, cancellationToken);
            return;
        }

        await MutateAsync(
            matchId,
            TimeSpan.Zero,
            current =>
            {
                if (current.Status != MatchStatus.Drafting || current.Deadline > now)
                {
                    return Task.FromResult<MatchChange?>(null);
                }

                var result = Domain(() => MatchRules.AutoChooseBlessings(current.State));
                return Task.FromResult<MatchChange?>(Advance(current, result.State, result.Events, now, roundResolved: false, NoDefaults));
            },
            cancellationToken);
    }

    /// <summary>Elige 1 de las 2 bendiciones de la oferta vigente. Repetir la misma elección devuelve el mismo ACK.</summary>
    public async Task<MatchResult<DraftAck>> DraftPickAsync(
        Guid matchId, Guid userId, Guid offerId, Blessing blessing, CancellationToken cancellationToken)
    {
        var previous = await hot.FindDraftAckAsync(matchId, userId, offerId, cancellationToken);
        if (previous is not null)
        {
            return previous.Blessing == blessing
                ? MatchResult<DraftAck>.Ok(previous)
                : new MatchError(RealtimeErrors.DraftClosed, "Ya elegiste una bendición de esta oferta.");
        }

        var found = await GetForPlayerAsync(matchId, userId, cancellationToken);
        if (found.Error is { } notFound)
        {
            return notFound;
        }

        var (snapshot, participant) = found.Value;
        if (!snapshot.Status.IsActive())
        {
            return new MatchError(RealtimeErrors.MatchNotActive, "La partida ya no está en curso.") { Status = snapshot.Status };
        }

        MatchError? error = null;
        var now = clock.UtcNow;
        var mutation = await MutateAsync(
            matchId,
            CommandLockWait,
            current =>
            {
                error = CheckDraft(current, participant.PlayerId, offerId, blessing, out var index);
                if (error is not null)
                {
                    return Task.FromResult<MatchChange?>(null);
                }

                var result = Domain(() => MatchRules.ChooseBlessing(current.State, participant.PlayerId, index));
                if (result.Error is { } domainError)
                {
                    error = Map(domainError, current.State.Round);
                    return Task.FromResult<MatchChange?>(null);
                }

                return Task.FromResult<MatchChange?>(Advance(current, result.State, result.Events, now, roundResolved: false, NoDefaults));
            },
            cancellationToken);

        if (error is not null)
        {
            return error;
        }

        if (mutation.Status != MutationStatus.Changed)
        {
            return MutationFailure(mutation);
        }

        var ack = new DraftAck(matchId, offerId, blessing, now);
        await hot.SaveDraftAckAsync(userId, ack, cancellationToken);
        if (mutation.Snapshot!.Status == MatchStatus.InProgress)
        {
            ScheduleCloseCheck(matchId, mutation.Snapshot.State.Round);
        }

        return MatchResult<DraftAck>.Ok(ack);
    }

    /// <summary>
    /// Abandono (voluntario con <c>LeaveMatch</c> o por gracia vencida): el héroe se retira y pierde sus
    /// ascuas. Devuelve si contó como abandono de una partida en curso.
    /// </summary>
    public async Task<MatchResult<bool>> RetireAsync(Guid matchId, Guid userId, CancellationToken cancellationToken)
    {
        var found = await GetForPlayerAsync(matchId, userId, cancellationToken);
        if (found.Error is { } error)
        {
            return error;
        }

        var (snapshot, participant) = found.Value;
        if (!snapshot.Status.IsActive())
        {
            return new MatchError(RealtimeErrors.MatchNotActive, "La partida ya no está en curso.") { Status = snapshot.Status };
        }

        var now = clock.UtcNow;
        var mutation = await MutateAsync(
            matchId,
            CommandLockWait,
            current =>
            {
                if (current.State.FindHero(participant.PlayerId) is not { IsOnBoard: true })
                {
                    return Task.FromResult<MatchChange?>(null);
                }

                var result = Domain(() => MatchRules.Retire(current.State, participant.PlayerId));
                return Task.FromResult(result.IsSuccess
                    ? Advance(current, result.State, result.Events, now, roundResolved: false, NoDefaults)
                    : null);
            },
            cancellationToken);

        if (mutation.Status is MutationStatus.Busy)
        {
            return MutationFailure(mutation);
        }

        if (mutation.Status == MutationStatus.Changed)
        {
            logger.LogInformation("Jugador {PlayerId} abandonó la partida {MatchId} en la ronda {Round}", userId, matchId, snapshot.State.Round);
            await notifier.PlayerConnectionChangedAsync(matchId, userId, ConnectionState.Abandoned, null, cancellationToken);
            await ReleaseActiveMatchAsync(matchId, [userId]);
            if (mutation.Snapshot!.Status == MatchStatus.InProgress)
            {
                ScheduleCloseCheck(matchId, mutation.Snapshot.State.Round);
            }
        }

        return MatchResult<bool>.Ok(mutation.Status == MutationStatus.Changed && snapshot.Status == MatchStatus.InProgress);
    }

    /// <summary>Venció la gracia: si el jugador no volvió, se aplica la regla de abandono del dominio.</summary>
    public async Task ExpireGraceAsync(Guid matchId, Guid userId, CancellationToken cancellationToken)
    {
        var connected = await hot.ConnectedUsersAsync(matchId, cancellationToken);
        if (connected.Contains(userId))
        {
            return;
        }

        var result = await RetireAsync(matchId, userId, cancellationToken);
        if (result.Error?.Code == RealtimeErrors.InternalError)
        {
            // El lock estaba ocupado: se reintenta en el próximo ciclo del temporizador.
            await hot.ScheduleGraceAsync(matchId, userId, clock.UtcNow + TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    /// <summary>Limpieza: una partida viva sin cambios por demasiado tiempo pasa a EXPIRED.</summary>
    public async Task ExpireAsync(Guid matchId, CancellationToken cancellationToken)
    {
        await using var lease = await hot.TryLockAsync(matchId, TimeSpan.Zero, cancellationToken);
        if (lease is null)
        {
            return;
        }

        var snapshot = await RepairFromStoreAsync(matchId, cancellationToken);
        if (snapshot is null || !snapshot.Status.IsActive())
        {
            return;
        }

        logger.LogInformation("La partida {MatchId} expiró por inactividad en la ronda {Round}", matchId, snapshot.State.Round);
        await StopAsync(snapshot, MatchStatus.Expired, cancellationToken);
    }

    /// <summary>
    /// Un administrador detiene la partida: con el lock de la partida (ninguna ronda se cierra a la vez)
    /// pasa a TERMINATED, se inserta la auditoría que arma <paramref name="audit"/> en la misma transacción,
    /// se libera la "partida activa" de los jugadores y se les avisa (<c>MatchFinished</c> + <c>KICKED_BY_ADMIN</c>).
    /// </summary>
    public async Task<MatchResult<MatchSnapshot>> TerminateAsync(
        Guid matchId, Func<MatchSnapshot, AuditEntry> audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var lease = await hot.TryLockAsync(matchId, CommandLockWait, cancellationToken);
        if (lease is null)
        {
            return new MatchError(RealtimeErrors.InternalError, "La partida está ocupada; reintente.");
        }

        var snapshot = await RepairFromStoreAsync(matchId, cancellationToken);
        if (snapshot is null)
        {
            return new MatchError(RealtimeErrors.MatchNotFound, "La partida no existe.");
        }

        if (!snapshot.Status.IsActive())
        {
            return new MatchError(RealtimeErrors.MatchNotActive, "La partida ya no está en curso.") { Status = snapshot.Status };
        }

        var stopped = await StopAsync(snapshot, MatchStatus.Terminated, cancellationToken, audit(snapshot));
        if (stopped is null)
        {
            return new MatchError(RealtimeErrors.MatchNotActive, "La partida terminó mientras se procesaba la orden.");
        }

        logger.LogInformation("La partida {MatchId} fue terminada por un administrador en la ronda {Round}", matchId, snapshot.State.Round);
        return MatchResult<MatchSnapshot>.Ok(stopped);
    }

    /// <summary>Una partida con el plazo vencido en Postgres que nadie cerró (p. ej. Redis perdió sus datos).</summary>
    public async Task RecoverAsync(Guid matchId, CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(matchId, cancellationToken);
        if (snapshot is not null)
        {
            await hot.SaveAsync(snapshot, cancellationToken);
            await ProcessDeadlineAsync(matchId, cancellationToken);
        }
    }

    /// <summary>Id estable de la oferta de draft vigente de un héroe (cambia con cada oferta nueva).</summary>
    public static Guid OfferIdFor(Guid matchId, Hero hero)
    {
        ArgumentNullException.ThrowIfNull(hero);
        var key = $"{matchId:N}|{hero.Player}|{hero.Blessings.Count}|{string.Join(',', hero.DraftOptions)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static MatchError? CheckDraft(MatchSnapshot snapshot, PlayerId player, Guid offerId, Blessing blessing, out int index)
    {
        index = -1;
        var hero = snapshot.State.FindHero(player);
        if (hero is null)
        {
            return new MatchError(RealtimeErrors.NotParticipant, "No participas en esta partida.");
        }

        if (!hero.IsOnBoard)
        {
            return new MatchError(RealtimeErrors.PlayerDown, "El héroe ya no está en la partida.");
        }

        if (hero.DraftOptions.IsEmpty || OfferIdFor(snapshot.MatchId, hero) != offerId)
        {
            return new MatchError(RealtimeErrors.DraftClosed, "La oferta ya se resolvió o venció.");
        }

        index = hero.DraftOptions.IndexOf(blessing);
        return index < 0 ? new MatchError(RealtimeErrors.InvalidPick, "Esa bendición no está en la oferta.") { Field = "blessingId" } : null;
    }

    private async Task<bool> AllDecidedAsync(MatchSnapshot snapshot, CancellationToken cancellationToken)
    {
        var active = snapshot.ActivePlayers();
        if (active.Count == 0)
        {
            return true;
        }

        var orders = await hot.GetOrdersAsync(snapshot.MatchId, snapshot.State.Round, cancellationToken);
        var connected = await hot.ConnectedUsersAsync(snapshot.MatchId, cancellationToken);

        // Un desconectado (en gracia) no frena la ronda: recibirá GUARDAR. Si nadie decidió, se espera el plazo.
        return active.Any(orders.ContainsKey)
            && active.All(p => orders.ContainsKey(p) || !connected.Contains(PlayerIds.ToUserId(p)));
    }

    private void ScheduleCloseCheck(Guid matchId, int round) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await TryCloseRoundAsync(matchId, round, deadlineExpired: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // El temporizador cerrará la ronda al vencer el plazo; aquí solo se registra.
                logger.LogWarning(ex, "No se pudo cerrar la ronda {Round} de la partida {MatchId}", round, matchId);
            }
        });

    private MatchChange Advance(
        MatchSnapshot current,
        GameState next,
        IReadOnlyList<DomainEvent> events,
        DateTimeOffset now,
        bool roundResolved,
        IReadOnlySet<PlayerId> defaults)
    {
        var status = MatchStatusExtensions.From(next);
        var deadline = status switch
        {
            MatchStatus.Drafting => current.Deadline,
            MatchStatus.InProgress when current.Status != MatchStatus.InProgress || current.State.Round != next.Round => now + current.RoundDuration,
            MatchStatus.InProgress => current.Deadline,
            _ => (DateTimeOffset?)null,
        };

        var after = current with { State = next, Status = status, Version = current.Version + 1, Deadline = deadline };
        return new MatchChange(current, after, events, roundResolved, defaults);
    }

    private async Task<Mutation> MutateAsync(
        Guid matchId, TimeSpan lockWait, Func<MatchSnapshot, Task<MatchChange?>> decide, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await using var lease = await hot.TryLockAsync(matchId, lockWait, cancellationToken);
        if (lease is null)
        {
            return new Mutation(MutationStatus.Busy, null);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            // En el reintento se parte de Postgres: la instantánea de Redis estaba atrasada.
            var current = attempt == 0
                ? await GetAsync(matchId, cancellationToken)
                : await RepairFromStoreAsync(matchId, cancellationToken);
            if (current is null || !current.Status.IsActive())
            {
                return new Mutation(MutationStatus.Unchanged, current);
            }

            var decided = Stopwatch.GetTimestamp();
            MatchChange? change;
            try
            {
                change = await decide(current);
            }
            catch (MatchFaultException ex)
            {
                await FaultAsync(current, ex.InnerException ?? ex, cancellationToken);
                return new Mutation(MutationStatus.Faulted, null);
            }

            if (change is null)
            {
                return new Mutation(MutationStatus.Unchanged, current);
            }

            if (!await store.CommitAsync(current, change.After, change.Events, cancellationToken))
            {
                logger.LogWarning(
                    "Conflicto al confirmar la partida {MatchId} (versión {Version}); se recarga desde Postgres",
                    matchId, current.Version);
                continue;
            }

            if (change.RoundResolved)
            {
                metrics.RoundResolved(Stopwatch.GetElapsedTime(decided));
            }

            await hot.SaveAsync(change.After, cancellationToken);
            if (!change.After.Status.IsActive())
            {
                await ReleaseActiveMatchAsync(matchId, [.. change.After.Participants.Select(p => p.UserId)]);
            }

            using (logger.BeginScope(new Dictionary<string, object>
            {
                ["MatchId"] = matchId,
                ["Round"] = current.State.Round,
                ["Seq"] = change.After.State.LastSeq,
            }))
            {
                logger.LogInformation(
                    "Partida confirmada: {EventCount} eventos, estado {Status}, versión {Version}",
                    change.Events.Count, change.After.Status, change.After.Version);
            }

            try
            {
                await notifier.MatchChangedAsync(change, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // La ronda ya está confirmada: un fallo de difusión se recupera con GetSnapshot.
                logger.LogWarning(ex, "Falló la difusión de la partida {MatchId} (seq {Seq})", matchId, change.After.State.LastSeq);
            }

            if (change.RoundResolved)
            {
                metrics.Broadcast(Stopwatch.GetElapsedTime(started));
            }

            return new Mutation(MutationStatus.Changed, change.After);
        }

        return new Mutation(MutationStatus.Busy, null);
    }

    private async Task<MatchSnapshot?> RepairFromStoreAsync(Guid matchId, CancellationToken cancellationToken)
    {
        var snapshot = await store.LoadAsync(matchId, cancellationToken);
        if (snapshot is not null)
        {
            await hot.SaveAsync(snapshot, cancellationToken);
        }

        return snapshot;
    }

    private async Task FaultAsync(MatchSnapshot current, Exception exception, CancellationToken cancellationToken)
    {
        // Aislamiento: la excepción de una partida la marca FAULTED y no afecta al resto ni al proceso.
        logger.LogError(exception, "La partida {MatchId} falló en la ronda {Round} y queda aislada como FAULTED", current.MatchId, current.State.Round);
        metrics.MatchFaulted();
        await StopAsync(current, MatchStatus.Faulted, cancellationToken);
    }

    private async Task<MatchSnapshot?> StopAsync(
        MatchSnapshot current, MatchStatus status, CancellationToken cancellationToken, AuditEntry? audit = null)
    {
        var changed = await store.MarkStatusAsync(current.MatchId, status, clock.UtcNow, audit, cancellationToken);
        if (!changed && audit is not null)
        {
            // Otra réplica la terminó antes: sin cambio no hay nada que auditar ni que avisar.
            return null;
        }

        var stopped = current with { Status = status, Version = current.Version + 1, Deadline = null };
        await hot.SaveAsync(stopped, cancellationToken);
        await ReleaseActiveMatchAsync(current.MatchId, [.. current.Participants.Select(p => p.UserId)]);
        await notifier.MatchStoppedAsync(stopped, cancellationToken);
        return stopped;
    }

    /// <summary>
    /// Quita la marca "partida activa" del lobby para que los jugadores puedan volver a salas o a la cola.
    /// Si Redis falla, la marca igual vence por su TTL (Ascua:Lobby:ActiveMatchTtl).
    /// </summary>
    private async Task ReleaseActiveMatchAsync(Guid matchId, IReadOnlyList<Guid> userIds)
    {
        foreach (var userId in userIds)
        {
            try
            {
                await activeMatches.ReleaseAsync(userId, matchId, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "No se pudo liberar la partida activa {MatchId} del jugador {PlayerId}", matchId, userId);
            }
        }
    }

    private static T Domain<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MatchFaultException(ex);
        }
    }

    private static MatchError Map(DomainError error, int currentRound) => error.Kind switch
    {
        ErrorCode.StaleRound => new MatchError(RealtimeErrors.StaleRound, error.Message) { CurrentRound = currentRound },
        ErrorCode.NotAParticipant => new MatchError(RealtimeErrors.NotParticipant, error.Message),
        ErrorCode.PlayerDown or ErrorCode.PlayerOut => new MatchError(RealtimeErrors.PlayerDown, error.Message),
        ErrorCode.MatchNotInProgress => new MatchError(RealtimeErrors.MatchNotActive, error.Message),
        ErrorCode.InvalidDraftChoice => new MatchError(RealtimeErrors.InvalidPick, error.Message),
        _ => new MatchError(RealtimeErrors.InvalidOrder, error.Message) { Field = "order.type" },
    };

    private static MatchError MutationFailure(Mutation mutation) => mutation.Status switch
    {
        MutationStatus.Faulted => new MatchError(RealtimeErrors.MatchNotActive, "La partida quedó en estado FAULTED y se detuvo.") { Status = MatchStatus.Faulted },
        MutationStatus.Unchanged => new MatchError(RealtimeErrors.MatchNotActive, "La partida ya no admite esta operación.") { Status = mutation.Snapshot?.Status },
        _ => new MatchError(RealtimeErrors.InternalError, "La partida está ocupada; reintente."),
    };

    private enum MutationStatus
    {
        Busy,
        Unchanged,
        Changed,
        Faulted,
    }

    private sealed record Mutation(MutationStatus Status, MatchSnapshot? Snapshot);

    private sealed class MatchFaultException(Exception inner) : Exception("Falla del dominio al mutar la partida.", inner);
}
