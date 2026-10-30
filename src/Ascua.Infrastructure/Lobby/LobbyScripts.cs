namespace Ascua.Infrastructure.Lobby;

/// <summary>
/// Scripts Lua del lobby. Redis ejecuta cada uno de forma atómica (nadie más corre entre sus comandos), así que
/// las comprobaciones "¿cabe?", "¿está libre este jugador?" y la escritura ocurren como una sola operación.
/// <para>
/// Claves (prefijo <see cref="LobbyKeys.Prefix"/>): <c>room:{id}</c> (hash v/data), <c>rooms:public</c> (zset),
/// <c>roomcode:{código}</c>, <c>user:{id}:lobby</c> (<c>room:{id}</c> | <c>queue</c> | <c>pending:{grupo}</c>),
/// <c>user:{id}:active_match</c>, <c>user:{id}:mm_expired</c>, <c>mm:queue</c> (zset por llegada),
/// <c>mm:tickets</c> (hash), <c>mm:group:{id}</c> y <c>mm:pending</c> (zset).
/// </para>
/// Los scripts arman claves a partir de ids recibidos en ARGV: requieren Redis sin modo clúster (ADR-0007).
/// </summary>
internal static class LobbyScripts
{
    /// <summary>
    /// <c>busy(uid, allowed)</c>: nil si el jugador está libre (o su marca de lobby es <c>allowed</c>); si no, el motivo
    /// (<c>MATCH</c>, <c>ROOM</c>, <c>QUEUE</c>). Una marca que apunta a una sala, cola o grupo que ya no existe se
    /// considera vencida y se borra.
    /// </summary>
    private const string Busy = """
        local P = ARGV[1]
        local function lobby_key(uid) return P .. 'user:' .. uid .. ':lobby' end
        local function busy(uid, allowed)
          if redis.call('EXISTS', P .. 'user:' .. uid .. ':active_match') == 1 then return 'MATCH' end
          local lobby = redis.call('GET', lobby_key(uid))
          if not lobby or lobby == allowed then return nil end
          if string.sub(lobby, 1, 5) == 'room:' then
            if redis.call('EXISTS', P .. lobby) == 1 then return 'ROOM' end
          elseif lobby == 'queue' then
            if redis.call('ZSCORE', P .. 'mm:queue', uid) then return 'QUEUE' end
          elseif string.sub(lobby, 1, 8) == 'pending:' then
            if redis.call('EXISTS', P .. 'mm:group:' .. string.sub(lobby, 9)) == 1 then return 'QUEUE' end
          end
          redis.call('DEL', lobby_key(uid))
          return nil
        end
        local function del_if(key, value)
          if redis.call('GET', key) == value then redis.call('DEL', key) end
        end
        """;

    /// <summary>
    /// Guarda una sala con control de versión. ARGV: 1 prefijo, 2 roomId, 3 versión esperada (0 = crear),
    /// 4 JSON nuevo ('' = borrar), 5 TTL ms, 6 listada (1/0), 7 score (creación ms), 8 código ('' = ninguno),
    /// 9 partida activa ('' = ninguna), 10 TTL partida ms, 11 n agregados, 12 n que siguen, 13 n que salen, luego los ids.
    /// Devuelve {'OK', versión} | {'CONFLICT'} | {'BUSY', motivo} | {'CODE'}.
    /// </summary>
    public const string SaveRoom = Busy + """

        local rid, expected, json, ttl = ARGV[2], ARGV[3], ARGV[4], tonumber(ARGV[5])
        local listed, score, code, match, match_ttl = ARGV[6], ARGV[7], ARGV[8], ARGV[9], tonumber(ARGV[10])
        local n_added, n_kept, n_removed = tonumber(ARGV[11]), tonumber(ARGV[12]), tonumber(ARGV[13])
        local room_key, marker = P .. 'room:' .. rid, 'room:' .. rid
        local code_key = P .. 'roomcode:' .. code

        local current = redis.call('HGET', room_key, 'v') or '0'
        if current ~= expected then return {'CONFLICT'} end

        local added, kept, removed = {}, {}, {}
        local i = 14
        for _ = 1, n_added do table.insert(added, ARGV[i]); i = i + 1 end
        for _ = 1, n_kept do table.insert(kept, ARGV[i]); i = i + 1 end
        for _ = 1, n_removed do table.insert(removed, ARGV[i]); i = i + 1 end

        for _, uid in ipairs(added) do
          local reason = busy(uid, marker)
          if reason then return {'BUSY', reason} end
        end

        if expected == '0' and code ~= '' then
          if not redis.call('SET', code_key, rid, 'NX', 'PX', ttl) then return {'CODE'} end
        end

        for _, uid in ipairs(removed) do del_if(lobby_key(uid), marker) end

        if json == '' then
          redis.call('DEL', room_key)
          redis.call('ZREM', P .. 'rooms:public', rid)
          if code ~= '' then del_if(code_key, rid) end
          return {'OK', 0}
        end

        local version = tonumber(current) + 1
        redis.call('HSET', room_key, 'v', version, 'data', json)
        redis.call('PEXPIRE', room_key, ttl)
        if listed == '1' then
          redis.call('ZADD', P .. 'rooms:public', score, rid)
        else
          redis.call('ZREM', P .. 'rooms:public', rid)
        end

        if match ~= '' then
          -- La partida arrancó: cada jugador sale del lobby y queda con su partida activa.
          for _, uid in ipairs(kept) do
            redis.call('SET', P .. 'user:' .. uid .. ':active_match', match, 'NX', 'PX', match_ttl)
            del_if(lobby_key(uid), marker)
          end
          if code ~= '' then del_if(code_key, rid) end
        else
          for _, uid in ipairs(added) do redis.call('SET', lobby_key(uid), marker, 'PX', ttl) end
          for _, uid in ipairs(kept) do redis.call('SET', lobby_key(uid), marker, 'PX', ttl) end
          if code ~= '' then redis.call('PEXPIRE', code_key, ttl) end
        end
        return {'OK', version}
        """;

    /// <summary>ARGV: 1 prefijo, 2 uid, 3 llegada ms, 4 ticket JSON. Devuelve 'OK' o el motivo de <c>busy</c>.</summary>
    public const string Enqueue = Busy + """

        local uid, now, ticket = ARGV[2], ARGV[3], ARGV[4]
        local queue = P .. 'mm:queue'
        if redis.call('ZSCORE', queue, uid) and redis.call('GET', lobby_key(uid)) == 'queue' then return 'OK' end
        local reason = busy(uid, nil)
        if reason then return reason end
        redis.call('SET', lobby_key(uid), 'queue')
        redis.call('ZADD', queue, now, uid)
        redis.call('HSET', P .. 'mm:tickets', uid, ticket)
        redis.call('DEL', P .. 'user:' .. uid .. ':mm_expired')
        return 'OK'
        """;

    /// <summary>ARGV: 1 prefijo, 2 uid. Devuelve 'OK' o 'MATCHING' si ya fue agrupado.</summary>
    public const string Leave = Busy + """

        local uid = ARGV[2]
        local lobby = redis.call('GET', lobby_key(uid))
        if lobby and string.sub(lobby, 1, 8) == 'pending:'
          and redis.call('EXISTS', P .. 'mm:group:' .. string.sub(lobby, 9)) == 1 then
          return 'MATCHING'
        end
        if redis.call('ZREM', P .. 'mm:queue', uid) == 1 then redis.call('HDEL', P .. 'mm:tickets', uid) end
        if lobby == 'queue' then redis.call('DEL', lobby_key(uid)) end
        return 'OK'
        """;

    /// <summary>
    /// Agrupador. ARGV: 1 prefijo, 2 ahora ms, 3 espera máxima ms, 4 id de grupo.
    /// Toma 4 jugadores, o 2-3 si el más antiguo ya esperó la espera máxima. Devuelve el JSON del grupo o nil.
    /// </summary>
    public const string FormGroup = Busy + """

        local now, max_wait, gid = tonumber(ARGV[2]), tonumber(ARGV[3]), ARGV[4]
        local queue, tickets = P .. 'mm:queue', P .. 'mm:tickets'
        local head = redis.call('ZRANGE', queue, 0, 3, 'WITHSCORES')
        local n = #head / 2
        if n < 2 then return nil end
        if n < 4 and now - tonumber(head[2]) < max_wait then return nil end
        local players = {}
        for k = 1, n do
          local uid, score = head[2 * k - 1], head[2 * k]
          local ticket = redis.call('HGET', tickets, uid) or '{}'
          redis.call('ZREM', queue, uid)
          redis.call('HDEL', tickets, uid)
          redis.call('SET', lobby_key(uid), 'pending:' .. gid)
          table.insert(players, {id = uid, at = score, ticket = ticket})
        end
        local group = cjson.encode({formedAt = now, players = players})
        redis.call('SET', P .. 'mm:group:' .. gid, group)
        redis.call('ZADD', P .. 'mm:pending', now, gid)
        return group
        """;

    /// <summary>ARGV: 1 prefijo, 2 grupo, 3 partida, 4 TTL de la partida activa ms. Idempotente.</summary>
    public const string CompleteGroup = Busy + """

        local gid, match, ttl = ARGV[2], ARGV[3], tonumber(ARGV[4])
        local group_key = P .. 'mm:group:' .. gid
        local raw = redis.call('GET', group_key)
        if not raw then return 0 end
        for _, p in ipairs(cjson.decode(raw).players) do
          redis.call('SET', P .. 'user:' .. p.id .. ':active_match', match, 'NX', 'PX', ttl)
          del_if(lobby_key(p.id), 'pending:' .. gid)
        end
        redis.call('DEL', group_key)
        redis.call('ZREM', P .. 'mm:pending', gid)
        return 1
        """;

    /// <summary>ARGV: 1 prefijo, 2 grupo. Devuelve a la cola, con su antigüedad, a los jugadores del grupo.</summary>
    public const string AbortGroup = Busy + """

        local gid = ARGV[2]
        local group_key = P .. 'mm:group:' .. gid
        local raw = redis.call('GET', group_key)
        if not raw then return 0 end
        for _, p in ipairs(cjson.decode(raw).players) do
          if redis.call('GET', lobby_key(p.id)) == 'pending:' .. gid then
            redis.call('ZADD', P .. 'mm:queue', p.at, p.id)
            redis.call('HSET', P .. 'mm:tickets', p.id, p.ticket)
            redis.call('SET', lobby_key(p.id), 'queue')
          end
        end
        redis.call('DEL', group_key)
        redis.call('ZREM', P .. 'mm:pending', gid)
        return 1
        """;

    /// <summary>ARGV: 1 prefijo, 2 límite de llegada ms, 3 TTL de la marca "expiró" ms. Devuelve cuántos salieron.</summary>
    public const string Expire = Busy + """

        local before, marker_ttl = ARGV[2], tonumber(ARGV[3])
        local queue = P .. 'mm:queue'
        local ids = redis.call('ZRANGEBYSCORE', queue, '-inf', before)
        for _, uid in ipairs(ids) do
          redis.call('ZREM', queue, uid)
          redis.call('HDEL', P .. 'mm:tickets', uid)
          del_if(lobby_key(uid), 'queue')
          redis.call('SET', P .. 'user:' .. uid .. ':mm_expired', '1', 'PX', marker_ttl)
        end
        return #ids
        """;

    /// <summary>KEYS: 1 marca de partida activa. ARGV: 1 partida. Borra solo si sigue apuntando a esa partida.</summary>
    public const string ReleaseActiveMatch = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;
}
