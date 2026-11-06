using System.Globalization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Ascua.LoadTests;

/// <summary>
/// Una partida de 4 bots de principio a fin. Cada bot piensa 0..ThinkMaxMs y envía una orden válida.
/// Mide, por ronda, desde el disparo del cierre (llegada al servidor de la última orden, o el plazo)
/// hasta que el ÚLTIMO jugador recibe <c>RoundResolved</c>, y verifica que ninguna jugada se pierda ni se duplique.
/// </summary>
public sealed class MatchSession(AscuaApi api, LoadOptions options, Recorder recorder, IReadOnlyList<BotUser> users)
{
    private static readonly string[] Directions = ["N", "S", "E", "W"];

    private readonly Dictionary<int, RoundTracker> _rounds = [];
    private readonly List<Seat> _seats = [];
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Guid _matchId;

    public async Task<bool> PlayAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var user in users)
            {
                await api.EnsureFreshAsync(user, cancellationToken);
            }

            _matchId = await api.LaunchDevMatchAsync(users, cancellationToken);
            await Task.WhenAll(users.Select(user => JoinAsync(user, cancellationToken)));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            recorder.Add(Kind.SetupError);
            Console.Error.WriteLine($"[setup] {ex.GetType().Name}: {ex.Message}");
            await DisposeAsync();
            return false;
        }
        catch (OperationCanceledException)
        {
            await DisposeAsync();
            return true;
        }

        recorder.MatchStarted();
        var completed = false;
        try
        {
            await _finished.Task.WaitAsync(options.MatchTimeout, cancellationToken);
            completed = true;

            // Un RoundResolved puede llegar después de MatchFinished a otra conexión.
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"[partida {_matchId}] no terminó en {options.MatchTimeout}");
        }
        catch (OperationCanceledException)
        {
            // Fin de la etapa: la partida queda a medias, no es un error.
        }
        finally
        {
            recorder.MatchEnded();
            CheckIncompleteRounds(completed);
            await DisposeAsync();
        }

        return true;
    }

    private async Task JoinAsync(BotUser user, CancellationToken cancellationToken)
    {
        var ticket = await api.TicketAsync(user, _matchId, cancellationToken);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(options.BaseUrl, $"realtime/match?protocolVersion=4&access_token={ticket}"), o =>
            {
                o.SkipNegotiation = true;
                o.Transports = HttpTransportType.WebSockets;
            })
            .AddMessagePackProtocol()
            .Build();
        connection.ServerTimeout = TimeSpan.FromSeconds(60);

        var seat = new Seat(user, connection);
        lock (_seats)
        {
            _seats.Add(seat);
        }

        connection.On<RoundOpened>("RoundOpened", opened => OnRoundOpened(seat, opened));
        connection.On<RoundResolved>("RoundResolved", resolved => OnRoundResolved(seat, resolved));
        connection.On<SeqOnly>("Snapshot", snapshot => seat.SeeSeq(snapshot.Seq));
        connection.On<DraftOffer>("DraftOffer", offer => _ = Task.Run(() => PickAsync(seat, offer), CancellationToken.None));
        connection.On<PathOffer>("PathOffer", offer => _ = Task.Run(() => ChoosePathAsync(seat, offer), CancellationToken.None));
        connection.On<PactOffer>("PactOffer", offer => _ = Task.Run(() => ChoosePactAsync(seat, offer), CancellationToken.None));
        connection.On<MatchFinished>("MatchFinished", _ => _finished.TrySetResult());
        connection.On<ErrorPush>("Error", push => Console.Error.WriteLine($"[push] {push.Error?.Code}: {push.Error?.Message}"));

        await connection.StartAsync(cancellationToken);
        var join = await connection.InvokeAsync<JoinReply>("JoinMatch", new MatchRequest { MatchId = _matchId.ToString("D") }, cancellationToken);
        if (join.Error is { } error)
        {
            throw new InvalidOperationException($"JoinMatch: {error.Code} {error.Message}");
        }

        seat.PlayerId = join.PlayerId ?? user.Id.ToString("D");
        seat.SeeSeq(join.Snapshot?.Seq ?? 0);
    }

    private async Task PickAsync(Seat seat, DraftOffer offer)
    {
        try
        {
            await seat.Connection.InvokeAsync<DraftReply>("DraftPick", new DraftPickRequest
            {
                MatchId = _matchId.ToString("D"),
                OfferId = offer.OfferId,
                BlessingId = offer.Options[Random.Shared.Next(offer.Options.Count)],
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // El draft no es parte de la meta; si falla, el plazo elige por el bot.
        }
    }

    /// <summary>Elige misión al azar (si falla, el plazo del draft elige la primera).</summary>
    private async Task ChoosePactAsync(Seat seat, PactOffer offer)
    {
        try
        {
            await seat.Connection.InvokeAsync<DraftReply>("ChoosePact", new ChoosePactRequest
            {
                MatchId = _matchId.ToString("D"),
                OfferId = offer.OfferId,
                PactId = offer.Options[Random.Shared.Next(offer.Options.Count)].PactId,
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // El draft no es parte de la meta.
        }
    }

    /// <summary>Tras extinguirse, el bot elige camino al azar (si no, la ronda espera los 15 s del plazo).</summary>
    private async Task ChoosePathAsync(Seat seat, PathOffer offer)
    {
        try
        {
            await seat.Connection.InvokeAsync<DraftReply>("ChoosePath", new ChoosePathRequest
            {
                MatchId = _matchId.ToString("D"),
                OfferId = offer.OfferId,
                Path = offer.Options[Random.Shared.Next(offer.Options.Count)],
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Si falla, el plazo elige Amistad por el bot.
        }
    }

    private void OnRoundOpened(Seat seat, RoundOpened opened)
    {
        lock (_rounds)
        {
            var tracker = Tracker(opened.Round);
            tracker.Deadline ??= Parse(opened.Deadline);
            tracker.Expected = opened.ExpectedPlayers;
            tracker.PendingSubmits++;
        }

        _ = Task.Run(() => SubmitAsync(seat, opened.Round), CancellationToken.None);
    }

    private async Task SubmitAsync(Seat seat, int round)
    {
        try
        {
            await Task.Delay(Random.Shared.Next(options.ThinkMaxMs + 1));
            if (Random.Shared.NextDouble() < options.SkipRate)
            {
                return;
            }

            var reply = await SendAsync(seat, round, RandomOrder());
            if (reply?.Error?.Code == "INVALID_ORDER")
            {
                reply = await SendAsync(seat, round, new OrderPayload { Type = "GUARD" });
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            recorder.Add(Kind.OrderError);
            Console.Error.WriteLine($"[orden] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (_rounds)
            {
                Tracker(round).PendingSubmits--;
                TryComplete(round);
            }
        }
    }

    private async Task<SubmitReply?> SendAsync(Seat seat, int round, OrderPayload order)
    {
        if (seat.Connection.State != HubConnectionState.Connected)
        {
            return null;
        }

        recorder.Add(Kind.OrderSent);
        var sentAt = DateTime.UtcNow;
        var reply = await seat.Connection.InvokeAsync<SubmitReply>("SubmitOrder", new SubmitOrderRequest
        {
            MatchId = _matchId.ToString("D"),
            Round = round,
            ClientActionId = Guid.CreateVersion7().ToString("D"),
            Order = order,
        });
        var ackAt = DateTime.UtcNow;
        var rtt = (ackAt - sentAt).TotalMilliseconds;
        recorder.Add(Kind.OrderRtt, rtt);

        switch (reply.Error?.Code)
        {
            case null when reply.Result == "ACCEPTED":
                recorder.Add(Kind.OrderAccepted);
                var receivedAt = Parse(reply.ReceivedAt!);
                seat.SeeAck(sentAt, ackAt, receivedAt, recorder);
                lock (_rounds)
                {
                    Tracker(round).Accepted.Add(receivedAt);
                    seat.AcceptedRounds.Add(round);
                }

                break;
            case null:
                // Un clientActionId recién creado nunca puede ser DUPLICATE_ACTION.
                recorder.Add(Kind.Duplicate);
                break;
            case "STALE_ROUND" when HadDownedHeroes(round):
                // Un héroe caído no está entre los esperados: la ronda cierra sin él y su orden llega tarde.
                break;
            case "STALE_ROUND":
                recorder.Add(Kind.OrderStale);
                Console.Error.WriteLine($"[tardía] partida {_matchId} ronda {round}: {reply.Error.Message}");
                break;
            case "PLAYER_DOWN" or "MATCH_NOT_ACTIVE" or "INVALID_ORDER":
                // Esperables: héroe caído, partida terminada, reintento con GUARDAR.
                break;
            default:
                recorder.Add(Kind.OrderError);
                Console.Error.WriteLine($"[orden] {reply.Error.Code}: {reply.Error.Message}");
                break;
        }

        return reply;
    }

    private void OnRoundResolved(Seat seat, RoundResolved resolved)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        if (!seat.CheckSeq(resolved.PreviousSeq, resolved.Seq))
        {
            recorder.Add(Kind.SeqGap);
            Console.Error.WriteLine($"[seq] partida {_matchId} ronda {resolved.Round}: previousSeq {resolved.PreviousSeq}, esperado {seat.LastSeqBefore}");
        }

        var defaulted = resolved.Delta.Events.Any(e =>
            e.TryGetValue("type", out var type) && (string)type == "DEFAULT_GUARD_APPLIED"
            && e.TryGetValue("actorId", out var actor) && (string)actor == seat.PlayerId);

        lock (_rounds)
        {
            if (defaulted && seat.AcceptedRounds.Contains(resolved.Round))
            {
                recorder.Add(Kind.LostOrder);
                Console.Error.WriteLine($"[perdida] partida {_matchId} ronda {resolved.Round}: orden aceptada reemplazada por GUARDAR");
            }

            // Hora del servidor: con reloj compartido (mismo host) no se corrige; si no, se aplica el desfase estimado.
            var serverTime = options.EstimateClockOffset ? receivedAt.AddMilliseconds(seat.OffsetMs) : receivedAt;
            Tracker(resolved.Round).Receipts.Add(serverTime);
            TryComplete(resolved.Round);
        }
    }

    /// <summary>Con los 4 RoundResolved y todas las respuestas de SubmitOrder, la muestra de la ronda está completa.</summary>
    private void TryComplete(int round)
    {
        var tracker = Tracker(round);
        if (tracker.Done || tracker.PendingSubmits > 0 || tracker.Receipts.Count < users.Count)
        {
            return;
        }

        tracker.Done = true;
        recorder.Add(Kind.RoundCompleted);
        var allOrders = tracker.Expected > 0 && tracker.Accepted.Count >= tracker.Expected;
        DateTimeOffset? trigger = allOrders ? tracker.Accepted.Max() : tracker.Deadline;
        if (trigger is { } at)
        {
            var latency = (tracker.Receipts.Max() - at).TotalMilliseconds;
            recorder.Add(allOrders ? Kind.RoundLatency : Kind.RoundLatencyDeadline, latency);
        }
    }

    private void CheckIncompleteRounds(bool completed)
    {
        lock (_rounds)
        {
            foreach (var tracker in _rounds.Values.Where(t => !t.Done && t.Receipts.Count > 0))
            {
                // Si la etapa cortó la partida, solo cuentan las rondas que ya llevaban 3 s cerradas.
                if (completed || DateTimeOffset.UtcNow - tracker.Receipts.Min() > TimeSpan.FromSeconds(3))
                {
                    recorder.Add(Kind.MissingBroadcast, users.Count - tracker.Receipts.Count);
                }
            }
        }
    }

    private bool HadDownedHeroes(int round)
    {
        lock (_rounds)
        {
            return Tracker(round).Expected < users.Count;
        }
    }

    private RoundTracker Tracker(int round)
    {
        if (!_rounds.TryGetValue(round, out var tracker))
        {
            tracker = new RoundTracker();
            _rounds[round] = tracker;
        }

        return tracker;
    }

    private static OrderPayload RandomOrder() => Random.Shared.Next(4) switch
    {
        0 => new OrderPayload { Type = "GUARD" },
        1 => new OrderPayload { Type = "ATTACK", Direction = Directions[Random.Shared.Next(4)], Step = Directions[Random.Shared.Next(4)] },
        _ => new OrderPayload { Type = "MOVE", Direction = Directions[Random.Shared.Next(4)] },
    };

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private async Task DisposeAsync()
    {
        Seat[] seats;
        lock (_seats)
        {
            seats = [.. _seats];
        }

        foreach (var seat in seats)
        {
            try
            {
                await seat.Connection.DisposeAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Ya estaba cerrada.
            }
        }
    }

    private sealed class RoundTracker
    {
        public DateTimeOffset? Deadline { get; set; }

        public int Expected { get; set; }

        public int PendingSubmits { get; set; }

        public List<DateTimeOffset> Accepted { get; } = [];

        public List<DateTimeOffset> Receipts { get; } = [];

        public bool Done { get; set; }
    }

    private sealed class Seat(BotUser user, HubConnection connection)
    {
        private readonly Lock _gate = new();
        private long _lastSeq = -1;
        private double _bestRtt = double.MaxValue;

        public BotUser User { get; } = user;

        public HubConnection Connection { get; } = connection;

        public string PlayerId { get; set; } = user.Id.ToString("D");

        public HashSet<int> AcceptedRounds { get; } = [];

        public double OffsetMs { get; private set; }

        public long LastSeqBefore { get; private set; }

        /// <summary>Snapshot (ACK de Join o push tras un draft): el ACK puede procesarse después del push, por eso el máximo.</summary>
        public void SeeSeq(long seq)
        {
            lock (_gate)
            {
                _lastSeq = Math.Max(_lastSeq, seq);
            }
        }

        public bool CheckSeq(long previousSeq, long seq)
        {
            lock (_gate)
            {
                LastSeqBefore = _lastSeq;
                var ok = _lastSeq < 0 || previousSeq == _lastSeq;
                _lastSeq = Math.Max(_lastSeq, seq);
                return ok;
            }
        }

        /// <summary>Desfase estilo NTP con la muestra de menor RTT: receivedAt − punto medio del envío y el ACK.</summary>
        public void SeeAck(DateTime sentAt, DateTime ackAt, DateTimeOffset serverReceivedAt, Recorder recorder)
        {
            var rtt = (ackAt - sentAt).TotalMilliseconds;
            lock (_gate)
            {
                if (rtt >= _bestRtt)
                {
                    return;
                }

                _bestRtt = rtt;
                var midpoint = new DateTimeOffset(sentAt.AddMilliseconds(rtt / 2), TimeSpan.Zero);
                OffsetMs = (serverReceivedAt - midpoint).TotalMilliseconds;
            }

            recorder.Add(Kind.ClockOffset, OffsetMs);
        }
    }
}
