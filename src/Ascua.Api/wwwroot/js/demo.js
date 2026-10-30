// Modo demo (?demo=1): un "servidor" mínimo en el navegador que habla el mismo contrato que el
// hub real (mismos nombres de mensajes y campos, ids de los ejemplos de contracts/asyncapi.yaml).
// No es el motor de reglas: solo simula lo suficiente para probar el render, el teclado,
// los ACK, la idempotencia por clientActionId, la reconexión con GetSnapshot y el fin de partida.

export const DEMO_MATCH_ID = '0193a1c2-5e7f-7a10-8b2c-3d4e5f607182';
/** Id del jugador propio en la demo y el tutorial: los eventos simulados lo usan como autor. */
export const DEMO_PLAYER_ID = '0193a1b0-1111-7c00-9a00-00000000a001';
const ME_ID = DEMO_PLAYER_ID;
const BETO_ID = '0193a1b0-1111-7c00-9a00-00000000a002';
const CARO_ID = '0193a1b0-1111-7c00-9a00-00000000a003';
const OFFER_ID = '0193a1c3-0000-7d00-8000-0000000000f1';

const DIRS = { N: [0, -1], S: [0, 1], E: [1, 0], W: [-1, 0] };
const LAYOUT_KINDS = { '.': 'FLOOR', '#': 'WALL', C: 'CHEST', S: 'STAIRS', '^': 'SPIKES_ARMED', B: 'BRAZIER', A: 'ALTAR' };

/** Escenario de la demo. El tutorial (tutorial.js) arma el suyo con la misma forma. */
export const DEMO_SCENARIO = {
  roundMs: 20000,
  // x → 0..8; y ↓ 0..8.  # muro, C cofre, S escalera, ^ pinchos armados, B brasero, A altar
  layout: [
    '....#...S',
    '.B......#',
    '......^..',
    '...#.C...',
    '...#.....',
    '.....##..',
    '..#......',
    '.......C.',
    'A........',
  ],
  draftOptions: ['LAMPARA', 'BRASA_VIVA'],
  me: {
    playerId: ME_ID, displayName: 'ana', hero: 'CHISPA', state: 'ALIVE', hp: 4, maxHp: 4, shield: 0,
    position: { x: 4, y: 6 }, facing: 'N', embers: 0, charges: 0, abilityCooldown: 0, stealthRoundsLeft: 0,
    downRoundsLeft: null, blessings: [], pact: { pactId: 'OPEN_3_CHESTS', description: 'Abrir 3 cofres.' },
    decided: false, pendingOrder: null,
  },
  allies: [
    { playerId: BETO_ID, displayName: 'beto', hero: 'YUNQUE', state: 'ALIVE', hp: 6, maxHp: 6, position: { x: 4, y: 7 }, facing: 'N', embers: 1, blessingCount: 1, connected: true, decided: false },
    { playerId: CARO_ID, displayName: 'caro', hero: 'CUERVO', state: 'ALIVE', hp: 4, maxHp: 4, position: { x: 1, y: 8 }, facing: 'E', embers: 3, blessingCount: 1, connected: true, decided: false },
  ],
  /** Pactos de los aliados: solo se revelan en MatchFinished (la vista nunca los lleva). */
  allyPacts: {
    [BETO_ID]: { pactId: 'NEVER_DOWNED', description: 'Terminar sin haber caído.', fulfilled: true },
    [CARO_ID]: { pactId: 'BETRAY_TWICE_UNSEEN', description: 'Traicionar dos veces sin ser visto.', fulfilled: false },
  },
  enemies: [
    { enemyId: 12, kind: 'BALLESTERO', hp: 3, maxHp: 3, position: { x: 4, y: 0 }, facing: 'S', phase: null, telegraph: [] },
    { enemyId: 9, kind: 'CARRONERO', hp: 3, maxHp: 3, position: { x: 7, y: 4 }, facing: 'W', phase: null, telegraph: [] },
    { enemyId: 15, kind: 'GELATINA', hp: 5, maxHp: 5, position: { x: 1, y: 3 }, facing: 'E', phase: null, telegraph: [] },
  ],
  /** Si es true, la partida termina sola al quedarse sin enemigos (el tutorial decide cuándo terminar). */
  autoFinish: true,
  /** Si es true, los golpes nunca dejan al héroe en 0 (el tutorial no castiga equivocarse). */
  forgiving: false,
  /** Si es true, el draft no se ofrece al unirse: lo pide quien maneja el escenario con sendOffer(). */
  holdDraft: false,
};

const clone = (v) => JSON.parse(JSON.stringify(v));
const key = (x, y) => `${x},${y}`;
const manhattan = (a, b) => Math.abs(a.x - b.x) + Math.abs(a.y - b.y);
const inBounds = (x, y) => x >= 0 && y >= 0 && x < 9 && y < 9;

class DemoServer {
  constructor(transport, scenario) {
    this.t = transport;
    this.scenario = scenario;
    this.seq = 0;
    this.status = 'DRAFTING';
    this.round = 0;
    this.floor = 1;
    this.deadline = null;
    this.tiles = new Map();
    scenario.layout.forEach((row, y) => [...row].forEach((ch, x) => this.tiles.set(key(x, y), LAYOUT_KINDS[ch])));
    this.seen = new Set();
    this.me = clone(scenario.me);
    this.allies = clone(scenario.allies);
    this.enemies = new Map(scenario.enemies.map((e) => [e.enemyId, clone(e)]));
    this.enemies.forEach((e) => this.updateTelegraph(e));
    this.lastOrder = null;
    this.orders = new Map(); // clientActionId → ack
    this.roundOrder = null; // { clientActionId, order }
    this.botsDecided = new Set();
    this.timers = new Set();
    this.lastVisibleEnemies = new Set();
    this.offer = null;
    this.chestsOpened = 0;
  }

  // ───── utilidades ─────
  later(ms, fn) {
    const id = setTimeout(() => { this.timers.delete(id); fn(); }, ms);
    this.timers.add(id);
  }

  dispose() { this.timers.forEach(clearTimeout); this.timers.clear(); }

  radius() { return 3 + (this.me.blessings.includes('LAMPARA') ? 1 : 0); }

  visible(pos) { return manhattan(pos, this.me.position) <= this.radius(); }

  blocked(x, y) {
    if (!inBounds(x, y)) return true;
    const kind = this.tiles.get(key(x, y));
    if (kind === 'WALL' || kind === 'BRAZIER' || kind === 'CHEST' || kind === 'CHEST_OPENED' || kind === 'ALTAR') return true;
    if (this.me.position.x === x && this.me.position.y === y) return true;
    if (this.allies.some((a) => a.position && a.position.x === x && a.position.y === y)) return true;
    return [...this.enemies.values()].some((e) => e.position.x === x && e.position.y === y);
  }

  /** Casillas visibles ahora; se agregan a las recordadas. Devuelve las nuevas. */
  refreshSeen() {
    const fresh = [];
    for (let y = 0; y < 9; y++) {
      for (let x = 0; x < 9; x++) {
        if (this.visible({ x, y }) && !this.seen.has(key(x, y))) {
          this.seen.add(key(x, y));
          fresh.push({ x, y, kind: this.tiles.get(key(x, y)) });
        }
      }
    }
    return fresh;
  }

  enemyView(e) {
    return { ...clone(e), telegraph: e.telegraph.filter((p) => this.visible(p)) };
  }

  visibleEnemies() {
    return [...this.enemies.values()].filter((e) => this.visible(e.position));
  }

  view() {
    this.refreshSeen();
    return {
      status: this.status,
      round: this.round,
      floor: this.floor,
      roundDeadline: this.deadline,
      me: clone(this.me),
      allies: clone(this.allies),
      enemies: this.visibleEnemies().map((e) => this.enemyView(e)),
      tiles: [...this.seen].map((k) => { const [x, y] = k.split(',').map(Number); return { x, y, kind: this.tiles.get(k) }; }),
    };
  }

  snapshot() {
    this.lastVisibleEnemies = new Set(this.visibleEnemies().map((e) => e.enemyId));
    return { matchId: DEMO_MATCH_ID, seq: this.seq, serverTime: new Date().toISOString(), view: this.view() };
  }

  // ───── invocaciones ─────
  handle(method, p) {
    const err = (code, message, extra = {}) => ({ error: { code, message, ...extra } });
    if (!p || p.matchId !== DEMO_MATCH_ID) return err('MATCH_NOT_FOUND', 'La partida no existe.');
    switch (method) {
      case 'JoinMatch':
        if (this.status === 'DRAFTING' && !this.offer && !this.scenario.holdDraft) this.later(600, () => this.sendOffer());
        return { matchId: DEMO_MATCH_ID, playerId: ME_ID, seat: 1, snapshot: this.snapshot() };
      case 'GetSnapshot':
        return this.snapshot();
      case 'DraftPick':
        if (!this.offer || p.offerId !== this.offer.offerId) return err('DRAFT_CLOSED', 'La oferta ya se resolvió.');
        if (!this.offer.options.includes(p.blessingId)) return err('INVALID_PICK', 'Esa bendición no estaba en la oferta.');
        return this.pick(p.blessingId);
      case 'SubmitOrder':
        return this.submit(p, err);
      case 'LeaveMatch':
        this.dispose();
        return { matchId: DEMO_MATCH_ID, leftAt: new Date().toISOString(), abandoned: this.status === 'IN_PROGRESS' };
      default:
        return err('INVALID_PAYLOAD', `Método desconocido: ${method}`);
    }
  }

  sendOffer() {
    this.offer = {
      matchId: DEMO_MATCH_ID, offerId: OFFER_ID, source: 'INITIAL_DRAFT', options: [...this.scenario.draftOptions],
      deadline: new Date(Date.now() + 20000).toISOString(),
    };
    this.t.push('DraftOffer', this.offer);
    const offerId = this.offer.offerId;
    this.later(20000, () => { if (this.offer?.offerId === offerId) this.pick(this.offer.options[Math.floor(Math.random() * 2)]); });
  }

  pick(blessingId) {
    const ack = { matchId: DEMO_MATCH_ID, offerId: this.offer.offerId, blessingId, acceptedAt: new Date().toISOString() };
    this.me.blessings.push(blessingId);
    this.offer = null;
    this.status = 'IN_PROGRESS';
    this.seq += 1;
    this.later(400, () => this.t.push('Snapshot', this.snapshot()));
    this.later(900, () => this.openRound());
    return ack;
  }

  submit(p, err) {
    if (this.orders.has(p.clientActionId)) {
      return { ...this.orders.get(p.clientActionId), result: 'DUPLICATE_ACTION' };
    }
    if (this.status !== 'IN_PROGRESS') return err('MATCH_NOT_ACTIVE', 'La partida no está en curso.', { details: { status: this.status } });
    if (this.me.state !== 'ALIVE') return err('PLAYER_DOWN', 'Tu héroe no puede actuar.');
    if (p.round !== this.round || this.resolving) {
      return err('STALE_ROUND', `La ronda ${p.round} no es la abierta; la vigente es la ${this.round}.`, { details: { currentRound: this.round } });
    }
    const o = p.order ?? {};
    const directed = ['MOVE', 'ATTACK', 'ABILITY', 'INTERACT'].includes(o.type);
    if (!(directed && DIRS[o.direction]) && !(o.type === 'GUARD' && o.direction === undefined)) {
      return err('INVALID_PAYLOAD', 'La orden no cumple el esquema.', { details: { field: 'order' } });
    }
    if (o.type === 'ABILITY' && this.me.charges === 0) {
      return err('INVALID_ORDER', 'Descarga necesita al menos 1 carga.', { details: { field: 'order.type' } });
    }
    if (this.roundOrder) return err('ORDER_ALREADY_SUBMITTED', 'Ya hay una orden para esta ronda.');

    const ack = { matchId: DEMO_MATCH_ID, round: this.round, clientActionId: p.clientActionId, result: 'ACCEPTED', receivedAt: new Date().toISOString() };
    this.orders.set(p.clientActionId, ack);
    this.roundOrder = clone(o);
    this.me.decided = true;
    this.me.pendingOrder = clone(o);
    this.later(50, () => this.decided(ME_ID));
    return ack;
  }

  // ───── rondas ─────
  openRound() {
    if (this.status !== 'IN_PROGRESS') return;
    this.round += 1;
    this.resolving = false;
    this.roundOrder = null;
    this.botsDecided.clear();
    this.me.decided = false;
    this.me.pendingOrder = null;
    this.allies.forEach((a) => { a.decided = false; });
    const roundMs = this.scenario.roundMs;
    this.deadline = new Date(Date.now() + roundMs).toISOString();
    const round = this.round;
    this.t.push('RoundOpened', { matchId: DEMO_MATCH_ID, round, deadline: this.deadline, durationMs: roundMs, expectedPlayers: this.expected() });
    for (const ally of this.allies) {
      if (ally.state !== 'ALIVE') continue;
      this.later(1500 + Math.random() * 4000, () => { if (this.round === round && !this.resolving) this.decided(ally.playerId); });
    }
    this.later(roundMs, () => { if (this.round === round && !this.resolving) this.resolve(); });
  }

  expected() {
    return [this.me, ...this.allies].filter((h) => h.state === 'ALIVE').length;
  }

  decidedCount() {
    return (this.me.decided ? 1 : 0) + this.botsDecided.size;
  }

  decided(playerId) {
    if (playerId !== ME_ID) {
      this.botsDecided.add(playerId);
      const ally = this.allies.find((a) => a.playerId === playerId);
      if (ally) ally.decided = true;
    }
    this.t.push('PlayerDecided', { matchId: DEMO_MATCH_ID, round: this.round, playerId, decidedCount: this.decidedCount(), expectedPlayers: this.expected() });
    if (this.decidedCount() >= this.expected()) this.later(300, () => this.resolve());
  }

  resolve() {
    if (this.resolving || this.status !== 'IN_PROGRESS') return;
    this.resolving = true;
    const events = [];
    const previousSeq = this.seq;
    const me = this.me;
    const tilesChanged = [];

    // Orden del jugador (GUARDAR por defecto si no envió nada).
    const order = this.roundOrder ?? { type: 'GUARD' };
    this.lastOrder = { ...clone(order), byDefault: !this.roundOrder };
    if (!this.roundOrder && me.state === 'ALIVE') events.push({ type: 'DEFAULT_GUARD_APPLIED', actorId: ME_ID });
    let attacked = false;
    if (me.state === 'ALIVE') {
      const [dx, dy] = DIRS[order.direction] ?? [0, 0];
      if (order.type !== 'GUARD') me.facing = order.direction;
      if (order.type === 'MOVE') {
        const nx = me.position.x + dx;
        const ny = me.position.y + dy;
        if (this.blocked(nx, ny)) {
          events.push({ type: 'BOUNCED', actorId: ME_ID, position: clone(me.position) });
        } else {
          me.position = { x: nx, y: ny };
          events.push({ type: 'MOVED', actorId: ME_ID, position: clone(me.position) });
        }
      } else if (order.type === 'ATTACK') {
        attacked = true;
        // Chispa: primer objetivo a 3 casillas en línea; muros y braseros detienen el disparo.
        for (let i = 1; i <= 3; i++) {
          const x = me.position.x + dx * i;
          const y = me.position.y + dy * i;
          if (!inBounds(x, y) || ['WALL', 'BRAZIER'].includes(this.tiles.get(key(x, y)))) break;
          const target = [...this.enemies.values()].find((e) => e.position.x === x && e.position.y === y);
          if (target) { this.hit(target, 1, events); break; }
        }
        events.push({ type: 'ATTACKED', actorId: ME_ID, position: clone(me.position) });
      } else if (order.type === 'ABILITY') {
        attacked = true;
        const dmg = 1 + me.charges;
        me.charges = 0;
        for (const e of [...this.enemies.values()]) {
          const sameRow = e.position.y === me.position.y && Math.abs(e.position.x - me.position.x) <= 3;
          const sameCol = e.position.x === me.position.x && Math.abs(e.position.y - me.position.y) <= 3;
          if (sameRow || sameCol) this.hit(e, dmg, events);
        }
      } else if (order.type === 'INTERACT') {
        const x = me.position.x + dx;
        const y = me.position.y + dy;
        if (this.tiles.get(key(x, y)) === 'CHEST') {
          this.tiles.set(key(x, y), 'CHEST_OPENED');
          tilesChanged.push({ x, y, kind: 'CHEST_OPENED' });
          this.chestsOpened += 1;
          me.embers += 2;
          events.push({ type: 'CHEST_OPENED', actorId: ME_ID, position: { x, y } });
          events.push({ type: 'EMBERS_GAINED', actorId: ME_ID, amount: 2 });
        }
      }
      if (!attacked) me.charges = Math.min(3, me.charges + 1);
    }

    // Aliados (bots): un paso al azar.
    for (const ally of this.allies) {
      if (ally.state !== 'ALIVE' || !ally.position) continue;
      const dirs = Object.keys(DIRS);
      const d = dirs[Math.floor(Math.random() * dirs.length)];
      const nx = ally.position.x + DIRS[d][0];
      const ny = ally.position.y + DIRS[d][1];
      ally.facing = d;
      if (!this.blocked(nx, ny)) {
        ally.position = { x: nx, y: ny };
        if (this.visible(ally.position)) events.push({ type: 'MOVED', actorId: ally.playerId, position: clone(ally.position) });
      }
    }

    // Enemigos: golpean lo anunciado en la ronda anterior, se mueven y anuncian de nuevo.
    for (const e of this.enemies.values()) {
      if (me.state === 'ALIVE' && e.telegraph.some((p) => p.x === me.position.x && p.y === me.position.y)) {
        const dmg = order.type === 'GUARD' ? 0 : 1; // GUARDAR anula los golpes de 1.
        if (dmg > 0) {
          me.hp = Math.max(this.scenario.forgiving ? 1 : 0, me.hp - dmg);
          events.push({ type: 'DAMAGED', actorId: `enemy:${e.enemyId}`, targetId: ME_ID, amount: dmg });
          if (me.hp === 0) {
            me.state = 'DOWN';
            me.downRoundsLeft = 3;
            events.push({ type: 'DOWNED', targetId: ME_ID });
          }
        } else {
          events.push({ type: 'BLOCKED', actorId: ME_ID, targetId: `enemy:${e.enemyId}` });
        }
      }
      if (e.kind !== 'BALLESTERO') this.stepToward(e, me.position);
      this.updateTelegraph(e);
    }
    if (me.state === 'DOWN' && me.downRoundsLeft !== null) {
      me.downRoundsLeft = Math.max(0, me.downRoundsLeft - 1);
    }

    me.decided = false;
    me.pendingOrder = null;
    this.allies.forEach((a) => { a.decided = false; });
    this.deadline = null;

    // Delta de la vista de este jugador.
    tilesChanged.push(...this.refreshSeen());
    const nowVisible = this.visibleEnemies();
    const nowIds = new Set(nowVisible.map((e) => e.enemyId));
    const enemiesRemoved = [...this.lastVisibleEnemies].filter((id) => !nowIds.has(id));
    this.lastVisibleEnemies = nowIds;

    const visibleEvents = events.filter((ev) => !ev.position || this.visible(ev.position));
    this.seq += Math.max(1, events.length);
    const msg = {
      matchId: DEMO_MATCH_ID, round: this.round, previousSeq, seq: this.seq,
      delta: {
        status: this.status, floorChanged: null, me: clone(me), alliesChanged: clone(this.allies),
        enemiesUpserted: nowVisible.map((e) => this.enemyView(e)), enemiesRemoved, tilesChanged, events: visibleEvents,
      },
    };
    this.t.push('RoundResolved', msg);

    if ((this.scenario.autoFinish && this.enemies.size === 0) || me.state !== 'ALIVE' || this.round >= 60) {
      this.later(800, () => this.finish(this.enemies.size === 0 ? 'WARDEN_DEFEATED' : 'EMBER_EXTINGUISHED'));
    } else {
      this.later(1200, () => this.openRound());
    }
  }

  hit(enemy, dmg, events) {
    enemy.hp = Math.max(0, enemy.hp - dmg);
    events.push({ type: 'DAMAGED', actorId: ME_ID, targetId: `enemy:${enemy.enemyId}`, amount: dmg });
    if (enemy.hp === 0) {
      this.enemies.delete(enemy.enemyId);
      this.me.embers += 1;
      events.push({ type: 'ENEMY_KILLED', actorId: ME_ID, targetId: `enemy:${enemy.enemyId}` });
      events.push({ type: 'EMBERS_GAINED', actorId: ME_ID, amount: 1 });
    }
  }

  stepToward(e, target) {
    const options = Object.entries(DIRS)
      .map(([d, [dx, dy]]) => ({ d, x: e.position.x + dx, y: e.position.y + dy }))
      .filter((o) => !this.blocked(o.x, o.y))
      .sort((a, b) => manhattan(a, target) - manhattan(b, target));
    const best = options[0];
    if (best && manhattan(best, target) < manhattan(e.position, target)) {
      e.position = { x: best.x, y: best.y };
      e.facing = best.d;
    }
  }

  updateTelegraph(e) {
    const tele = [];
    if (e.kind === 'BALLESTERO') {
      const [dx, dy] = DIRS[e.facing];
      for (let i = 1; i <= 3; i++) {
        const x = e.position.x + dx * i;
        const y = e.position.y + dy * i;
        if (!inBounds(x, y) || this.tiles.get(key(x, y)) === 'WALL') break;
        tele.push({ x, y });
      }
    } else {
      // Cuerpo a cuerpo: anuncia la casilla adyacente hacia el héroe más cercano.
      const target = this.me.position;
      let best = null;
      for (const [d, [dx, dy]] of Object.entries(DIRS)) {
        const p = { x: e.position.x + dx, y: e.position.y + dy };
        if (!inBounds(p.x, p.y)) continue;
        if (!best || manhattan(p, target) < manhattan(best.p, target)) best = { d, p };
      }
      if (best) { tele.push(best.p); e.facing = best.d; }
    }
    e.telegraph = tele;
  }

  myPactFulfilled() {
    switch (this.me.pact.pactId) {
      case 'OPEN_3_CHESTS': return this.chestsOpened >= 3;
      case 'OPEN_1_CHEST': return this.chestsOpened >= 1;
      case 'NEVER_DOWNED': return this.me.state === 'ALIVE';
      default: return false;
    }
  }

  /** Agrega un enemigo en una casilla libre (el tutorial lo usa para preparar cada lección). */
  spawn(enemy) {
    if (this.blocked(enemy.position.x, enemy.position.y)) return false;
    const e = { facing: 'S', phase: null, telegraph: [], maxHp: enemy.hp, ...clone(enemy) };
    this.enemies.set(e.enemyId, e);
    this.updateTelegraph(e);
    return true;
  }

  finish(outcome) {
    if (this.status !== 'IN_PROGRESS' && this.status !== 'DRAFTING') return;
    this.status = 'FINISHED';
    this.dispose();
    this.seq += 1;
    const me = this.me;
    const standings = [
      { playerId: me.playerId, displayName: me.displayName, hero: me.hero, finalState: me.state, embers: me.embers, pact: { ...me.pact, fulfilled: this.myPactFulfilled() } },
      ...this.allies.map((a) => ({ playerId: a.playerId, displayName: a.displayName, hero: a.hero, finalState: a.state, embers: a.embers, pact: this.scenario.allyPacts?.[a.playerId] ?? null })),
    ].sort((a, b) => b.embers - a.embers).map((s, i) => ({ ...s, placement: i + 1 }));
    this.t.push('MatchFinished', {
      matchId: DEMO_MATCH_ID, seq: this.seq, outcome, rounds: this.round,
      winners: outcome === 'WARDEN_DEFEATED' ? [standings[0].playerId] : [],
      standings, finishedAt: new Date().toISOString(),
      replayUrl: `${location.origin}/api/v1/matches/${DEMO_MATCH_ID}/replay`,
    });
    this.later(300, () => this.t.serverClose('MATCH_FINISHED: la partida terminó.'));
  }
}

/** Implementa la misma interfaz que RealTransport (hub.js). */
export class DemoTransport {
  constructor(scenario = DEMO_SCENARIO) {
    this.matchId = DEMO_MATCH_ID;
    this.handlers = new Map();
    this.closeHandler = () => {};
    this.isConnected = false;
    this.server = new DemoServer(this, scenario);
    this.loseNextAck = false;
    this.protocolName = 'demo';
  }

  on(type, handler) { this.handlers.set(type, handler); }
  onClose(handler) { this.closeHandler = handler; }
  get connected() { return this.isConnected; }

  async connect() {
    await new Promise((r) => setTimeout(r, 250));
    this.isConnected = true;
  }

  async invoke(method, payload) {
    if (!this.isConnected) throw new Error('Sin conexión');
    await new Promise((r) => setTimeout(r, 60 + Math.random() * 120));
    const result = clone(this.server.handle(method, clone(payload)));
    if (method === 'SubmitOrder' && this.loseNextAck && !result.error) {
      // El servidor registró la orden, pero la conexión se cae antes de entregar el ACK.
      this.loseNextAck = false;
      this.serverClose('INTERNAL_ERROR: caída simulada antes del ACK.');
      throw new Error('Conexión perdida antes del ACK');
    }
    return result;
  }

  /** Entrega un mensaje del servidor (at-most-once: si no hay conexión, se pierde). */
  push(type, payload) {
    if (!this.isConnected) return;
    this.handlers.get(type)?.(clone(payload));
  }

  serverClose(error) {
    if (!this.isConnected) return;
    this.isConnected = false;
    const code = String(error).split(':')[0];
    this.closeHandler({ code, message: error });
  }

  async stop() {
    this.isConnected = false;
  }

  // ───── botones de la demo ─────
  /** Si hay una ronda abierta sin orden propia, la próxima orden pierde su ACK; si no, cae ya. */
  simulateDrop() {
    const s = this.server;
    if (s.status === 'IN_PROGRESS' && !s.roundOrder && !s.resolving) {
      this.loseNextAck = true;
      return 'La próxima orden llegará al servidor, pero la conexión caerá antes del ACK.';
    }
    this.serverClose('INTERNAL_ERROR: caída simulada.');
    return 'Conexión cortada.';
  }

  simulateFinish() { this.server.finish('WARDEN_DEFEATED'); }

  dispose() { this.server.dispose(); }
}
