// Modo demo (?demo=1): un "servidor" mínimo en el navegador que habla el mismo contrato que el
// hub real (mismos nombres de mensajes y campos, ids de los ejemplos de contracts/asyncapi.yaml).
// No es el motor de reglas: solo simula lo suficiente para probar el render, el teclado,
// los ACK, la idempotencia por clientActionId, la reconexión con GetSnapshot y el fin de partida.
//
// También simula los caminos tras la extinción (planteamiento/04-caminos-tras-morir.md): la oferta
// (PathOffer) y ChoosePath, la Brasa errante con sus cinco órdenes, la Calidez y los ecos, el Renegado
// (propio o un aliado), bajar de piso (renacer y aparición) y «El Umbral reclama». Los botones de la demo
// (simulateExtinction, simulateRenegadeAlly, simulateDescend) los disparan sin esperar.
//
// Los nueve héroes se imitan en demo-heroes.js. ?demo=1&hero=ESCUCHA elige el héroe propio y &allies=USURERA,AVARO los de los bots.
import { ROOM_TYPES } from './labels.js';
import {
  BELL_MS, afterRound, beforeEnemies, correctedStrike, finalBetrayals, heroAbility, heroAttack, heroMove, heroStats, hostKilled, hostOrder, miserDamage, newHeroState,
  pickAllies, placeSecondBody,
} from './demo-heroes.js';
import { HERO_INFO } from './heroes.js';

export const DEMO_MATCH_ID = '0193a1c2-5e7f-7a10-8b2c-3d4e5f607182';
/** Id del jugador propio en la demo y el tutorial: los eventos simulados lo usan como autor. */
export const DEMO_PLAYER_ID = '0193a1b0-1111-7c00-9a00-00000000a001';
const ME_ID = DEMO_PLAYER_ID;
const BETO_ID = '0193a1b0-1111-7c00-9a00-00000000a002';
const CARO_ID = '0193a1b0-1111-7c00-9a00-00000000a003';
const DANI_ID = '0193a1b0-1111-7c00-9a00-00000000a004';
const OFFER_ID = '0193a1c3-0000-7d00-8000-0000000000f1';
const PACT_OFFER_ID = '0193a1c3-0000-7d00-8000-0000000000f2';
const PUSH_WAIT = 3;
const PHASE_ORDER = ['REVEAL', 'GUARD', 'SHIFT', 'MOVEMENT', 'ACTIONS', 'ENEMY_ATTACKS', 'ENEMY_MOVES', 'CLEANUP', 'END'];
const NOT_PUSHABLE = new Set(['CELADOR', 'TOTEM']);
/** Ofertas de camino: una por extinción (prefijo + número de extinción). */
const PATH_OFFER_PREFIX = '0193a1c4-0000-7d00-8000-';

const DIRS = { N: [0, -1], S: [0, 1], E: [1, 0], W: [-1, 0] };
const LAYOUT_KINDS = { '.': 'FLOOR', '#': 'WALL', C: 'CHEST', S: 'STAIRS', '^': 'SPIKES_ARMED', B: 'BRAZIER', A: 'ALTAR' };

// Reglas de los caminos que la demo imita (las de verdad están en src/Ascua.Domain/Rules/Paths.cs).
const PATH_DEADLINE_MS = 15000;
const REVIVE_HP = 2;
const KINDLE_COOLDOWN = 1;
const HINDER_COOLDOWN = 2;
const WARMTH_EVERY = 3;
/** Ascuas al renacer: ⌈guardadas × (25% + 8% × ecos)⌉, tope 85%. */
const rebirthEmbers = (saved, echoes) => Math.ceil((saved * Math.min(85, 25 + 8 * echoes)) / 100);
const SIGNAL_INTERVAL_MS = 3000;

/** Escenario de la demo. El tutorial (tutorial.js) arma el suyo con la misma forma. */
export const DEMO_SCENARIO = {
  roundMs: 20000,
  // x → 0..8; y ↓ 0..8.  # muro, C cofre, S escalera, ^ pinchos armados, B brasero, A altar
  layout: [
    '....#....',
    '.B......#',
    '......^..',
    '...#S.C..',
    '...#.....',
    '.....##..',
    '..#......',
    '.......C.',
    'A........',
  ],
  draftOptions: ['LAMPARA', 'BRASA_VIVA'],
  pactOptions: [
    { pactId: 'OPEN_3_CHESTS', description: 'Abrir 3 cofres.' },
    { pactId: 'OUTGOLD_TARGET', description: 'Terminar con más oro que tu objetivo.', targetId: BETO_ID },
  ],
  curses: ['FIERCE'],
  me: {
    playerId: ME_ID, displayName: 'ana', hero: 'ERRATA', state: 'ALIVE', hp: 4, maxHp: 4, shield: 0,
    position: { x: 4, y: 6 }, facing: 'N', embers: 0, abilityCooldown: 0,
    downRoundsLeft: null, blessings: [], pact: { pactId: 'OPEN_3_CHESTS', description: 'Abrir 3 cofres.' },
    decided: false, pendingOrder: null, path: null,
  },
  allies: [
    { playerId: BETO_ID, displayName: 'beto', hero: 'HILVAN', state: 'ALIVE', hp: 8, maxHp: 8, position: { x: 4, y: 7 }, facing: 'N', embers: 1, blessingCount: 1, connected: true, decided: false },
    { playerId: CARO_ID, displayName: 'caro', hero: 'POLIZON', state: 'ALIVE', hp: 6, maxHp: 6, position: { x: 1, y: 7 }, facing: 'E', embers: 3, blessingCount: 1, connected: true, decided: false },
    { playerId: DANI_ID, displayName: 'dani', hero: 'CAMPANERO', state: 'ALIVE', hp: 4, maxHp: 4, position: { x: 7, y: 7 }, facing: 'W', embers: 2, blessingCount: 1, connected: true, decided: false },
  ],
  /** Pactos de los aliados: solo se revelan en MatchFinished (la vista nunca los lleva). */
  allyPacts: {
    [BETO_ID]: { pactId: 'REVIVE_2_ALLIES', description: 'Levantar a 2 aliados con Usar.', fulfilled: true },
    [CARO_ID]: { pactId: 'BETRAY_TWICE_UNCONVICTED', description: 'Traicionar dos veces sin que te condenen.', fulfilled: false },
    [DANI_ID]: { pactId: 'OPEN_3_CHESTS', description: 'Abrir 3 cofres.', fulfilled: false },
  },
  enemies: [
    { enemyId: 12, kind: 'BALLESTERO', hp: 3, maxHp: 3, position: { x: 4, y: 0 }, facing: 'S', phase: null, telegraph: [] },
    { enemyId: 9, kind: 'CARRONERO', hp: 3, maxHp: 3, position: { x: 7, y: 4 }, facing: 'W', phase: null, telegraph: [] },
    { enemyId: 15, kind: 'GELATINA', hp: 5, maxHp: 5, position: { x: 1, y: 3 }, facing: 'E', phase: null, telegraph: [] },
  ],
  /** Salas a las que se baja con «Bajar de piso» (se repiten en ciclo). */
  nextFloors: [
    {
      layout: [
        '.........',
        '.#.....#.',
        '...B.....',
        '....S#...',
        '.C...#...',
        '.........',
        '..#...^..',
        '.........',
        '.........',
      ],
      enemies: [
        { enemyId: 31, kind: 'CARRONERO', hp: 3, position: { x: 2, y: 1 } },
        { enemyId: 32, kind: 'SALTASAPO', hp: 3, position: { x: 6, y: 2 } },
        { enemyId: 33, kind: 'POLILLA', hp: 2, position: { x: 4, y: 4 } },
      ],
    },
    {
      layout: [
        '#.......#',
        '.........',
        '..#...#..',
        '....S.C..',
        '.^.....^.',
        '.........',
        '..B...B..',
        '.........',
        '.........',
      ],
      enemies: [
        { enemyId: 41, kind: 'BALLESTERO', hp: 3, position: { x: 4, y: 1 } },
        { enemyId: 42, kind: 'GELATINA', hp: 5, position: { x: 1, y: 3 } },
        { enemyId: 43, kind: 'CARRONERO', hp: 3, position: { x: 7, y: 3 } },
      ],
    },
    {
      // Sala del Celador: golpea una ronda sí y una no; en la otra toma aire.
      layout: [
        '.........',
        '.B.....B.',
        '.........',
        '....S....',
        '.........',
        '.........',
        '..#...#..',
        '.........',
        '.........',
      ],
      enemies: [{ enemyId: 51, kind: 'CELADOR', hp: 15, position: { x: 4, y: 3 }, phase: 1 }],
    },
  ],
  /** Si es true, la partida termina sola al quedarse sin enemigos (el tutorial decide cuándo terminar). */
  autoFinish: true,
  /** Si es true, los golpes nunca dejan al héroe en 0 (el tutorial no castiga equivocarse). */
  forgiving: false,
  /** Si es true, el draft no se ofrece al unirse: lo pide quien maneja el escenario con sendOffer(). */
  holdDraft: false,
};

/** Casillas de llegada del grupo al bajar de piso, por asiento (la Brasa renace en la suya). */
const ENTRANCE = [{ x: 4, y: 7 }, { x: 3, y: 7 }, { x: 5, y: 7 }, { x: 2, y: 7 }];

/** Escenario de ?demo=1 con el héroe propio y los de los bots que pida la dirección. */
export function demoScenario() {
  const params = new URLSearchParams(location.search);
  const wanted = params.get('hero');
  const hero = HERO_INFO[wanted] ? wanted : 'ERRATA';
  const roster = pickAllies(hero, params.get('allies')?.split(','));
  const me = { ...DEMO_SCENARIO.me, hero, ...heroStats(hero) };
  const allies = DEMO_SCENARIO.allies.map((a, i) => ({ ...a, hero: roster[i], ...heroStats(roster[i]), embers: HERO_INFO[roster[i]].hpIsEmbers ? 4 : a.embers }));
  // La Usurera necesita a alguien herido a quien prestarle.
  if (hero === 'USURERA') allies[0].hp = Math.max(1, allies[0].hp - 3);
  return { ...DEMO_SCENARIO, me, allies };
}

const clone = (v) => JSON.parse(JSON.stringify(v));
const key = (x, y) => `${x},${y}`;
const manhattan = (a, b) => Math.abs(a.x - b.x) + Math.abs(a.y - b.y);
const inBounds = (x, y) => x >= 0 && y >= 0 && x < 9 && y < 9;
const same = (a, b) => Boolean(a && b) && a.x === b.x && a.y === b.y;
const iso = (ms) => new Date(ms).toISOString();

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
    this.loadLayout(scenario.layout);
    this.seen = new Set();
    this.me = clone(scenario.me);
    this.me.path = this.me.path ?? null;
    this.me.pushCooldown = 0;
    if (scenario.pactOptions) this.me.pact = null;
    this.pactOffer = null;
    this.room = { type: 'NORMAL', name: ROOM_TYPES.NORMAL.name, effect: ROOM_TYPES.NORMAL.effect, floodedRows: 0, size: 9 };
    this.roundsInRoom = 0;
    this.floorBetrayals = new Map(); // traidor → [{ victimId, damage }] de esta sala: lo que se puede juzgar
    this.pushedOnto = [];
    this.allies = clone(scenario.allies);
    this.heroes = newHeroState();
    this.signalAt = 0;
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
    // Caminos.
    this.pathOffer = null; // { offerId, choice, ack, deadline, auto, practice, saved }
    this.extinctions = 0;
    this.flags = { extinguish: false, renegadeAlly: false, descend: false };
    this.forceNext = false;
    this.paused = false;
    this.kindled = new Map(); // héroe → escudo de brasas que le dio el Fantasma propia (máx. 2)
    this.warned = new Set(); // escudo de aviso ya usado: `${renegado}>${héroe}`
    this.warmth = new Map(); // héroe → ronda desde la que puede volver a recibir Calidez
    this.hindered = new Set(); // enemigos estorbados esta ronda
    this.beacons = []; // Faros de esta resolución (dan visión al grupo)
    this.allyPlans = new Map(); // aliado extinto → { reveal, path, spawn }
    this.allyLast = new Map(); // última casilla de cada aliado (para revelar su Brasa ahí)
    this.allyCooldown = new Map(); // enfriamiento de Escudo de un Fantasma aliado
    if (this.me.hero === 'HILVAN') placeSecondBody(this, this.me);
    for (const a of this.allies) if (a.hero === 'HILVAN') placeSecondBody(this, a);
  }

  // ───── utilidades ─────
  later(ms, fn) {
    const id = setTimeout(() => { this.timers.delete(id); fn(); }, ms);
    this.timers.add(id);
  }

  dispose() { this.timers.forEach(clearTimeout); this.timers.clear(); }

  loadLayout(layout) {
    this.tiles = new Map();
    layout.forEach((row, y) => [...row].forEach((ch, x) => this.tiles.set(key(x, y), LAYOUT_KINDS[ch])));
  }

  isBlind() { return this.me.hero === 'ESCUCHA' && this.me.state !== 'EMBER_SPIRIT'; }

  radius() { return this.me.blessings.includes('LAMPARA') ? 18 : (this.me.state === 'EMBER_SPIRIT' ? 4 : this.isBlind() ? 1 : 3); }

  /** Bando de cada portador: el grupo (héroes y Brasas) o la mazmorra (Renegados). */
  side(h) { return h.state === 'RENEGADE' ? 'dungeon' : 'group'; }

  visible(pos) {
    if (this.heroes.shout) return true;
    if (manhattan(pos, this.me.position) <= this.radius()) return true;
    if (this.me.secondBody && manhattan(pos, this.me.secondBody) <= this.radius()) return true;
    // La Escucha oye lo que hizo ruido en cualquier parte de la sala.
    if (this.isBlind() && this.heroes.noise.some((p) => same(p, pos))) return true;
    // El Faro de una Brasa da visión a todo el grupo, no a los Renegados.
    return this.side(this.me) === 'group' && this.beacons.some((b) => manhattan(pos, b) <= 2);
  }

  meAwaiting() { return this.me.state === 'RENEGADE' && Boolean(this.me.path?.awaitingSpawn); }

  meCanAct() {
    const s = this.me.state;
    return s === 'ALIVE' || s === 'EMBER_SPIRIT' || (s === 'RENEGADE' && !this.meAwaiting());
  }

  allyCanAct(a) { return a.state === 'ALIVE' || a.state === 'EMBER_SPIRIT' || (a.state === 'RENEGADE' && Boolean(a.position)); }

  /** Héroes activos: a quienes golpean los monstruos y los que cuentan para el fin (ni Brasas ni Renegados). */
  heroTargets() { return [this.me, ...this.allies].filter((h) => h.state === 'ALIVE' && h.position); }

  /** ¿Ocupa su casilla? La Brasa es intangible; el extinto y el Renegado que espera no están en el tablero. */
  solid(h) {
    if (!h.position) return false;
    if (h.state === 'ALIVE' || h.state === 'DOWN') return true;
    return h.state === 'RENEGADE' && !(h === this.me && this.meAwaiting());
  }

  blocked(x, y) {
    if (!inBounds(x, y)) return true;
    const kind = this.tiles.get(key(x, y));
    if (kind === 'WALL' || kind === 'BRAZIER' || kind === 'CHEST' || kind === 'CHEST_OPENED' || kind === 'ALTAR') return true;
    if (this.solid(this.me) && this.me.hostEnemyId == null && this.me.position.x === x && this.me.position.y === y) return true;
    if (this.me.secondBody && this.me.secondBody.x === x && this.me.secondBody.y === y) return true;
    if (this.allies.some((a) => this.solid(a) && ((a.position.x === x && a.position.y === y) || (a.secondBody && a.secondBody.x === x && a.secondBody.y === y)))) return true;
    return [...this.enemies.values()].some((e) => e.position.x === x && e.position.y === y);
  }

  /** Casillas visibles ahora; se agregan a las recordadas. Devuelve las nuevas. */
  refreshSeen() {
    this.me.visionRadius = this.radius();
    const fresh = [];
    for (let y = 0; y < 9; y++) {
      for (let x = 0; x < 9; x++) {
        // La Escucha conoce el terreno de toda la sala.
        if ((this.isBlind() || this.visible({ x, y })) && !this.seen.has(key(x, y))) {
          this.seen.add(key(x, y));
          fresh.push({ x, y, kind: this.tiles.get(key(x, y)) });
        }
      }
    }
    return fresh;
  }

  enemyView(e) {
    const telegraph = e.enemyId === this.me.hostEnemyId ? [] : e.telegraph.filter((p) => this.sees(p));
    const out = { ...clone(e), telegraph };
    delete out.correctedBy;
    delete out.boardedRound;
    return out;
  }

  /** Errata ve todos los anuncios, aunque estén en la niebla. */
  sees(tile) { return this.me.hero === 'ERRATA' || this.me.blessings.includes('OJO_DE_BUHO') || this.visible(tile); }

  /** Casillas anunciadas por enemigos que no se ven, sin autor. */
  hiddenTelegraphs() {
    if (this.me.hero !== 'ERRATA' && !this.me.blessings.includes('OJO_DE_BUHO')) return [];
    return [...this.enemies.values()].filter((e) => !this.visible(e.position)).flatMap((e) => e.telegraph);
  }

  /** Lo que el jugador ve de los héroes: Ecos en su visión, ascuas en casillas visibles y el ruido si es La Escucha. */
  heroFields() {
    const h = this.heroes;
    return {
      hiddenTelegraphs: clone(this.hiddenTelegraphs()),
      echoes: clone(h.echoes.filter((e) => e.ownerId === ME_ID || this.visible(e.position))),
      groundEmbers: clone(h.ground.filter((g) => this.visible(g.position))),
      noiseTiles: this.isBlind() ? clone(h.noise) : [],
      bell: h.bell && this.status === 'IN_PROGRESS' ? { ringerId: h.bell.ringerId, deadline: h.bell.closed ? null : iso(h.bell.at), closed: h.bell.closed } : null,
    };
  }

  visibleEnemies() {
    return [...this.enemies.values()].filter((e) => this.visible(e.position));
  }

  /** Otro portador tal como lo ve el jugador: los del otro bando, solo dentro de la visión. */
  allyView(a) {
    const v = clone(a);
    const inHost = a.hero === 'POLIZON' && a.hiddenInHost;
    const heard = !this.isBlind() || this.visible(a.position ?? { x: -9, y: -9 });
    const shown = a.position && !inHost && heard && (this.side(a) === this.side(this.me) || this.visible(a.position));
    if (!shown) { v.position = null; v.facing = null; v.secondBody = null; }
    delete v.hiddenInHost;
    return v;
  }

  view() {
    this.refreshSeen();
    return {
      status: this.status,
      round: this.round,
      floor: this.floor,
      roundDeadline: this.deadlineOfMe(),
      me: clone(this.me),
      allies: this.allies.map((a) => this.allyView(a)),
      enemies: this.visibleEnemies().map((e) => this.enemyView(e)),
      tiles: [...this.seen].map((k) => { const [x, y] = k.split(',').map(Number); return { x, y, kind: this.tiles.get(k) }; }),
      room: clone(this.room),
      curses: [...(this.scenario.curses ?? [])],
      chillRound: 80,
      ...this.heroFields(),
    };
  }

  /** Plazo de este jugador: con el Repique de otro, el reducido. */
  deadlineOfMe() {
    const b = this.heroes.bell;
    return b && b.ringerId !== ME_ID && this.deadline ? iso(b.at) : this.deadline;
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
        this.resendPathOffer();
        return { matchId: DEMO_MATCH_ID, playerId: ME_ID, seat: 1, snapshot: this.snapshot() };
      case 'GetSnapshot':
        this.resendPathOffer();
        return this.snapshot();
      case 'DraftPick':
        if (!this.offer || p.offerId !== this.offer.offerId) return err('DRAFT_CLOSED', 'La oferta ya se resolvió.');
        if (!this.offer.options.includes(p.blessingId)) return err('INVALID_PICK', 'Esa mejora no estaba en la oferta.');
        return this.pick(p.blessingId);
      case 'ChoosePact': {
        if (!this.pactOffer || p.offerId !== this.pactOffer.offerId) return err('PACT_CLOSED', 'La oferta de misión ya se cerró.');
        const option = this.pactOffer.options.find((o) => o.pactId === p.pactId);
        if (!option) return err('INVALID_PAYLOAD', 'Esa misión no estaba en la oferta.');
        return this.pickPact(option);
      }
      case 'ChoosePath':
        return this.choosePath(p, err);
      case 'SubmitOrder':
        return this.submit(p, err);
      case 'SendSignal':
        return this.signal(p, err);
      case 'LeaveMatch':
        this.dispose();
        return { matchId: DEMO_MATCH_ID, leftAt: new Date().toISOString(), abandoned: this.status === 'IN_PROGRESS' };
      default:
        return err('INVALID_PAYLOAD', `Método desconocido: ${method}`);
    }
  }

  /** Señal rápida: máximo 1 cada 3 s; llega al bando (aquí, también a quien la envía). A veces un bot contesta. */
  signal(p, err) {
    if (!['HERE', 'DANGER', 'HELP', 'MINE'].includes(p.kind)) return err('INVALID_PAYLOAD', 'La señal no es válida.', { details: { field: 'kind' } });
    if (this.status !== 'IN_PROGRESS') return err('MATCH_NOT_ACTIVE', 'La partida no está en una ronda.');
    if (Date.now() - this.signalAt < SIGNAL_INTERVAL_MS) return err('RATE_LIMITED', 'Demasiadas señales: espera unos segundos.', { retryAfterMs: SIGNAL_INTERVAL_MS });
    this.signalAt = Date.now();
    const sentAt = new Date().toISOString();
    this.t.push('SignalReceived', { matchId: DEMO_MATCH_ID, fromPlayerId: ME_ID, kind: p.kind, position: p.position ?? null, sentAt });
    const bot = this.allies.find((a) => a.state === 'ALIVE' && a.position && !a.hiddenInHost);
    if (bot) this.later(900, () => this.t.push('SignalReceived', { matchId: DEMO_MATCH_ID, fromPlayerId: bot.playerId, kind: 'HERE', position: clone(bot.position), sentAt: new Date().toISOString() }));
    return { matchId: DEMO_MATCH_ID, sentAt };
  }

  sendOffer() {
    this.offer = {
      matchId: DEMO_MATCH_ID, offerId: OFFER_ID, source: 'INITIAL_DRAFT', options: [...this.scenario.draftOptions],
      deadline: new Date(Date.now() + 20000).toISOString(),
    };
    this.t.push('DraftOffer', this.offer);
    const offerId = this.offer.offerId;
    this.later(20000, () => { if (this.offer?.offerId === offerId) this.pick(this.offer.options[Math.floor(Math.random() * 2)]); });
    if (this.scenario.pactOptions) {
      this.pactOffer = { matchId: DEMO_MATCH_ID, offerId: PACT_OFFER_ID, options: clone(this.scenario.pactOptions), deadline: this.offer.deadline };
      this.t.push('PactOffer', this.pactOffer);
      this.later(20000, () => { if (this.pactOffer) this.pickPact(this.pactOffer.options[0]); });
    }
  }

  pickPact(option) {
    this.me.pact = clone(option);
    this.pactOffer = null;
    this.startWhenChosen();
    return { matchId: DEMO_MATCH_ID, result: 'ACCEPTED', pactId: option.pactId, acceptedAt: new Date().toISOString() };
  }

  /** La partida empieza cuando están elegidas la mejora y la misión. */
  startWhenChosen() {
    this.seq += 1;
    this.later(400, () => this.t.push('Snapshot', this.snapshot()));
    if (this.offer || this.pactOffer) return;
    this.status = 'IN_PROGRESS';
    this.later(900, () => this.openRound());
  }

  pick(blessingId) {
    const ack = { matchId: DEMO_MATCH_ID, offerId: this.offer.offerId, blessingId, acceptedAt: new Date().toISOString() };
    this.me.blessings.push(blessingId);
    this.offer = null;
    this.startWhenChosen();
    return ack;
  }

  // ───── caminos: oferta y elección ─────
  pushPathOffer() {
    const o = this.pathOffer;
    if (!o || o.choice) return;
    const saved = o.saved ?? this.me.path?.savedEmbers ?? 0;
    this.t.push('PathOffer', {
      matchId: DEMO_MATCH_ID, offerId: o.offerId, options: ['FRIENDSHIP', 'VENGEANCE'], defaultPath: 'FRIENDSHIP',
      deadline: iso(o.deadline ?? Date.now() + PATH_DEADLINE_MS), savedEmbers: saved, vengeanceEmbers: Math.ceil(saved / 2),
      friendshipEmbersMin: rebirthEmbers(saved, 0), friendshipEmbersMax: rebirthEmbers(saved, 8), wardenRoom: this.floor >= 5,
    });
  }

  /** Como el servidor: la oferta abierta se reenvía tras JoinMatch y GetSnapshot. */
  resendPathOffer() {
    if (this.pathOffer && !this.pathOffer.choice && this.pathOffer.deadline) this.later(250, () => this.pushPathOffer());
  }

  /** Oferta de práctica (tutorial): no cambia el héroe ni la ronda; solo muestra la elección. */
  sendPracticePathOffer(saved = 6, ms = 30000) {
    this.pathOffer = { offerId: `${PATH_OFFER_PREFIX}ffffffffffff`, choice: null, ack: null, practice: true, saved, deadline: Date.now() + ms };
    this.pushPathOffer();
    return this.pathOffer;
  }

  choosePath(p, err) {
    if (p.path !== 'FRIENDSHIP' && p.path !== 'VENGEANCE') return err('INVALID_PAYLOAD', 'El camino no es válido.', { details: { field: 'path' } });
    const o = this.pathOffer;
    if (!o || o.offerId !== p.offerId) return err('PATH_CLOSED', 'La elección ya cerró o es de otra extinción.');
    if (o.choice && o.choice !== p.path) return err('PATH_CLOSED', 'Ya elegiste otro camino.');
    if (o.choice) return { ...o.ack, result: 'DUPLICATE_ACTION' };
    o.choice = p.path;
    o.ack = { matchId: DEMO_MATCH_ID, offerId: o.offerId, path: p.path, result: 'ACCEPTED', acceptedAt: new Date().toISOString() };
    if (o.practice) return o.ack;
    this.me.path.choice = p.path;
    // La ronda esperaba la elección (como espera las órdenes).
    this.me.decided = true;
    this.later(50, () => this.decided(ME_ID));
    return o.ack;
  }

  autoChoose(offerId) {
    const o = this.pathOffer;
    if (!o || o.offerId !== offerId || o.choice || o.practice) return;
    o.choice = 'FRIENDSHIP';
    o.auto = true;
    this.me.path.choice = 'FRIENDSHIP';
    this.me.decided = true;
    this.t.push('Snapshot', this.snapshot());
    this.decided(ME_ID);
  }

  submit(p, err) {
    if (this.orders.has(p.clientActionId)) {
      return { ...this.orders.get(p.clientActionId), result: 'DUPLICATE_ACTION' };
    }
    if (this.status !== 'IN_PROGRESS') return err('MATCH_NOT_ACTIVE', 'La partida no está en curso.', { details: { status: this.status } });
    if (!this.meCanAct()) return err('PLAYER_DOWN', 'Tu héroe no puede actuar.');
    if (p.round !== this.round || this.resolving) {
      return err('STALE_ROUND', `La ronda ${p.round} no es la abierta; la vigente es la ${this.round}.`, { details: { currentRound: this.round } });
    }
    const o = p.order ?? {};
    const directed = ['MOVE', 'ATTACK', 'ABILITY', 'INTERACT', 'PUSH'].includes(o.type);
    if (!(directed && DIRS[o.direction]) && !(o.type === 'GUARD' && o.direction === undefined)) {
      return err('INVALID_PAYLOAD', 'La orden no cumple el esquema.', { details: { field: 'order' } });
    }
    if (o.step !== undefined && (!DIRS[o.step] || o.type === 'MOVE' || o.type === 'GUARD')) {
      return err('INVALID_PAYLOAD', 'El paso no vale con esa orden.', { details: { field: 'order.step' } });
    }
    if (o.type === 'PUSH' && (!['ALIVE', 'RENEGADE'].includes(this.me.state) || this.me.hostEnemyId != null || this.me.pushCooldown > 0)) {
      return err('INVALID_ORDER', 'Ahora no puedes empujar.', { details: { field: 'order.type' } });
    }
    if (this.me.state === 'EMBER_SPIRIT') {
      if (o.type === 'ABILITY' && this.me.abilityCooldown > 0) return err('INVALID_ORDER', 'Escudo está en espera.', { details: { field: 'order.type' } });
      if (o.type === 'ATTACK' && this.me.path.hinderCooldown > 0) return err('INVALID_ORDER', 'Frenar está en espera.', { details: { field: 'order.type' } });
    } else if (o.type === 'ABILITY' && this.me.abilityCooldown > 0 && this.me.hostEnemyId == null) {
      return err('INVALID_ORDER', 'La habilidad está en espera.', { details: { field: 'order.type' } });
    }
    const bell = this.heroes.bell;
    if (bell && bell.ringerId !== ME_ID && (bell.closed || Date.now() >= bell.at)) return err('ORDER_TOO_LATE', 'Venció tu plazo del Repique: defiendes esta ronda.');
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
    if (this.status !== 'IN_PROGRESS' || this.paused) return;
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
    // Repique de la ronda anterior: todos menos quien lo tocó deciden en 8 s.
    const ringer = this.heroes.bellNext;
    this.heroes.bellNext = null;
    this.heroes.bell = ringer ? { ringerId: ringer, at: Date.now() + BELL_MS, closed: false } : null;
    this.heroes.lastWordSent = false;
    this.t.push('RoundOpened', {
      matchId: DEMO_MATCH_ID, round, deadline: this.deadline, durationMs: roundMs, expectedPlayers: this.expected(),
      ...(ringer ? { bellDeadline: iso(this.heroes.bell.at), bellRingerId: ringer } : {}),
    });
    if (ringer) {
      this.later(BELL_MS, () => {
        if (this.round !== round || this.resolving) return;
        this.heroes.bell.closed = true;
        this.heroes.bellExpired = ringer;
        this.t.push('Snapshot', this.snapshot());
        // Si quien lo tocó ya decidió, no hay nada más que esperar.
        if (ringer !== ME_ID || this.me.decided) this.later(300, () => this.resolve());
      });
    }
    for (const ally of this.allies) {
      if (!this.allyCanAct(ally)) continue;
      this.later(1500 + Math.random() * 4000, () => { if (this.round === round && !this.resolving) this.decided(ally.playerId); });
    }
    // Elección de camino abierta: vence 15 s después de abrir la ronda (nunca después de su cierre).
    const o = this.pathOffer;
    if (o && !o.practice && !o.choice) {
      o.deadline = Date.now() + Math.min(PATH_DEADLINE_MS, roundMs);
      this.later(80, () => this.pushPathOffer());
      this.later(o.deadline - Date.now(), () => this.autoChoose(o.offerId));
    }
    this.later(roundMs, () => { if (this.round === round && !this.resolving) this.resolve(); });
    if (this.forceNext) {
      this.forceNext = false;
      this.later(400, () => { if (this.round === round && !this.resolving) this.resolve(); });
    }
  }

  expected() {
    const choosing = this.pathOffer && !this.pathOffer.practice && this.me.state === 'EXTINCT' ? 1 : 0;
    return (this.meCanAct() ? 1 : 0) + choosing + this.allies.filter((a) => this.allyCanAct(a)).length;
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
    if (this.decidedCount() >= this.expected()) {
      this.later(300, () => this.resolve());
    } else if (this.me.hero === 'CAMPANERO' && !this.me.decided && this.me.state !== 'EMBER_SPIRIT' && !this.heroes.lastWordSent && this.decidedCount() >= this.expected() - 1) {
      this.sendLastWord();
    }
  }

  /** Último en hablar: todos los demás decidieron y el Campanero todavía no. Como Renegado solo oye los tipos. */
  sendLastWord() {
    this.heroes.lastWordSent = true;
    const typesOnly = this.me.state === 'RENEGADE';
    const orders = this.allies.filter((a) => this.allyCanAct(a)).map((a, i) => {
      const type = ['MOVE', 'ATTACK', 'GUARD'][(this.round + i) % 3];
      return { playerId: a.playerId, type, direction: type === 'GUARD' || typesOnly ? null : Object.keys(DIRS)[(this.round + i) % 4] };
    });
    this.t.push('LastWord', { matchId: DEMO_MATCH_ID, round: this.round, typesOnly, orders });
  }

  /** Resuelve la ronda abierta ya (un botón de la demo preparó algo); si no hay ronda abierta, la próxima. */
  forceResolve() {
    if (this.status === 'IN_PROGRESS' && !this.resolving && this.deadline) this.later(150, () => this.resolve());
    else this.forceNext = true;
  }

  resolve() {
    if (this.resolving || this.status !== 'IN_PROGRESS' || this.paused) return;
    this.resolving = true;
    const events = [];
    const previousSeq = this.seq;
    const me = this.me;
    let tilesChanged = [];
    const wasDown = me.state === 'DOWN';

    // Inicio: se apagan los Faros y el Grito de la ronda anterior, y se revelan los caminos elegidos.
    this.beacons = [];
    this.heroes.shout = false;
    this.heroes.bell = null;
    const positionsBefore = new Map([...this.allies.map((a) => [a.playerId, a.position]), ...[...this.enemies.values()].map((e) => [`enemy:${e.enemyId}`, clone(e.position)])]);
    const revealed = this.revealPaths(events);
    const tag = (phase) => {
      for (const ev of events) {
        if (ev.phase) continue;
        if (ev.type === 'ROOM_SHIFTED') ev.phase = 'SHIFT';
        else if (phase === 'ACTIONS' && (ev.type === 'MOVED' || ev.type === 'BOUNCED')) ev.phase = 'MOVEMENT';
        else if (phase === 'ENEMY_ATTACKS' && (ev.type === 'MOVED' || ev.type === 'TELEGRAPHED')) ev.phase = 'ENEMY_MOVES';
        else ev.phase = phase;
      }
    };
    tag('REVEAL');

    // Orden del jugador (GUARDAR —el Faro, para la Brasa— por defecto si no envió nada).
    const order = this.roundOrder ?? { type: 'GUARD' };
    this.lastOrder = { ...clone(order), byDefault: !this.roundOrder };
    if (!revealed.has(ME_ID)) {
      if (order.step) this.stepFirst(order.step, events);
      tag('MOVEMENT');
      if (me.state === 'ALIVE') this.actHero(order, events, tilesChanged);
      else if (me.state === 'EMBER_SPIRIT') this.actSpirit(order, events);
      else if (me.state === 'RENEGADE' && !this.meAwaiting()) {
        if (order.type === 'PUSH') this.push(order, events);
        else this.actRenegade(order, events);
      }
    }
    this.actAllies(events, revealed);
    beforeEnemies(this, events);
    tag('ACTIONS');
    this.enemiesPhase(events, order);
    tag('ENEMY_ATTACKS');
    this.passives(events, wasDown);
    afterRound(this, events, positionsBefore);
    this.applyFlags(events);
    this.hazards(events);
    if (me.pushCooldown > 0) me.pushCooldown -= 1;
    const toNextRoom = this.flags.descend;
    this.flags.descend = false;
    tag('CLEANUP');
    // Llegan en orden de fase, como del servidor.
    events.sort((a, b) => PHASE_ORDER.indexOf(a.phase) - PHASE_ORDER.indexOf(b.phase));
    const floorChanged = null;

    me.decided = false;
    me.pendingOrder = null;
    this.allies.forEach((a) => { a.decided = false; });
    this.deadline = null;

    // Delta de la vista de este jugador.
    if (floorChanged) tilesChanged = [];
    tilesChanged.push(...this.refreshSeen());
    const nowVisible = this.visibleEnemies();
    const nowIds = new Set(nowVisible.map((e) => e.enemyId));
    const enemiesRemoved = floorChanged ? [] : [...this.lastVisibleEnemies].filter((id) => !nowIds.has(id));
    this.lastVisibleEnemies = nowIds;

    // Lo que ve: un suceso con casilla solo si la ve; la aparición de un Renegado llega a todos, sin casilla si no se ve.
    const visibleEvents = events.filter((ev) => ev.type === 'RENEGADE_SPAWNED' || !(ev._at ?? ev.position) || this.visible(ev._at ?? ev.position))
      .map((ev) => {
        const out = { ...ev };
        delete out._at;
        if (out.type === 'RENEGADE_SPAWNED' && out.position && !this.visible(out.position)) delete out.position;
        return out;
      });
    this.seq += Math.max(1, events.length);
    const msg = {
      matchId: DEMO_MATCH_ID, round: this.round, previousSeq, seq: this.seq,
      delta: {
        status: this.status, floorChanged, me: clone(me), alliesChanged: this.allies.map((a) => this.allyView(a)),
        enemiesUpserted: nowVisible.map((e) => this.enemyView(e)), enemiesRemoved, tilesChanged, events: visibleEvents,
        ...this.heroFields(),
        bell: null,
        roundDeadline: null,
      },
    };
    this.t.push('RoundResolved', msg);

    // Fin: sin enemigos (demo) o sin héroes en pie (las Brasas no cuentan). No hay límite de rondas.
    const renegadesUp = [me, ...this.allies].some((h) => h.state === 'RENEGADE' && h.position && !(h === me && this.meAwaiting()));
    if (toNextRoom) {
      this.later(150, () => this.nextRoom());
    } else if (this.scenario.autoFinish && this.enemies.size === 0) {
      this.later(800, () => this.finish('WARDEN_DEFEATED'));
    } else if (this.heroTargets().length === 0 && this.scenario.allies.length > 0) {
      this.later(800, () => this.finish(renegadesUp ? 'THRESHOLD_CLAIMED' : 'EMBER_EXTINGUISHED'));
    } else {
      this.later(1200, () => this.openRound());
    }
  }

  // ───── resolución: paso, sabotaje, empujón ─────
  /** El paso va antes que todas las acciones; la acción sale de donde quedó. */
  stepFirst(direction, events) {
    const me = this.me;
    const move = { type: 'MOVE', direction };
    if (me.state === 'EMBER_SPIRIT') this.actSpirit(move, events);
    else if (me.state === 'RENEGADE') { if (!this.meAwaiting()) this.actRenegade(move, events); }
    else if (me.state !== 'ALIVE') return;
    else if (me.hostEnemyId != null) hostOrder(this, move, events, ME_ID);
    else {
      me.facing = direction;
      heroMove(this, move, events, ME_ID);
    }
  }

  recordBetrayal(traitorId, victimId) {
    const k = `${traitorId}>${victimId}`;
    this.heroes.betrayals.set(k, (this.heroes.betrayals.get(k) ?? 0) + 1);
    const list = this.floorBetrayals.get(traitorId) ?? [];
    list.push({ victimId, damage: 1 });
    this.floorBetrayals.set(traitorId, list);
  }

  /** Mueve 1 casilla a lo adyacente. Sin efecto, no se gasta. El Traidor solo empuja héroes. */
  push(order, events) {
    const me = this.me;
    const [dx, dy] = DIRS[order.direction];
    const at = { x: me.position.x + dx, y: me.position.y + dy };
    const to = { x: at.x + dx, y: at.y + dy };
    const hero = this.allies.find((a) => a.position && same(a.position, at) && a.state !== 'EMBER_SPIRIT' && !a.hiddenInHost);
    const enemy = me.state === 'RENEGADE' ? null : [...this.enemies.values()].find((e) => same(e.position, at) && !NOT_PUSHABLE.has(e.kind));
    const target = hero ?? enemy;
    if (!target || !inBounds(to.x, to.y) || this.blocked(to.x, to.y)) return;
    target.position = clone(to);
    me.pushCooldown = PUSH_WAIT + 1; // baja 1 al terminar esta misma ronda
    this.pushedOnto.push({ hero, enemy: hero ? null : enemy });
    events.push({ type: 'PUSHED', actorId: ME_ID, targetId: hero ? hero.playerId : `enemy:${enemy.enemyId}`, position: clone(to) });
  }

  /** Limpieza: pinchos bajo quien fue empujado y agua de la sala inundada. Sin autor. */
  hazards(events) {
    for (const { hero, enemy } of this.pushedOnto) {
      const at = (hero ?? enemy).position;
      if (this.tiles.get(key(at.x, at.y)) !== 'SPIKES_ARMED') continue;
      if (hero) this.damageHero(hero, 1, null, events);
      else if (this.enemies.has(enemy.enemyId)) this.hit(enemy, 1, events, ME_ID);
    }
    this.pushedOnto = [];
    if (this.room.type !== 'FLOODED') return;
    this.roundsInRoom += 1;
    const rows = Math.floor(this.roundsInRoom / 6);
    if (rows > this.room.floodedRows) {
      this.room.floodedRows = rows;
      events.push({ type: 'ROOM_FLOODED', amount: rows });
    }
    if (!this.room.floodedRows) return;
    for (const h of [this.me, ...this.allies]) {
      if (h.state === 'ALIVE' && h.position && h.position.y < this.room.floodedRows) this.damageHero(h, 1, null, events);
    }
    for (const e of [...this.enemies.values()]) {
      if (e.position.y < this.room.floodedRows && e.kind !== 'POLILLA' && e.kind !== 'CELADOR') this.hit(e, 1, events, null);
    }
  }

  // ───── sala siguiente (sin votación: el tipo sale al azar) ─────
  nextRoom() {
    const events = [];
    this.descend(events);
    const types = Object.keys(ROOM_TYPES).filter((t) => t !== 'NORMAL' && t !== this.room.type);
    const type = this.floor >= 5 ? 'NORMAL' : types[Math.floor(Math.random() * types.length)];
    this.room = { type, name: this.floor >= 5 ? 'La sala del Celador' : ROOM_TYPES[type].name, effect: ROOM_TYPES[type].effect, floodedRows: 0, size: 9 };
    this.roundsInRoom = 0;
    this.floorBetrayals.clear();
    this.seq += 1;
    this.t.push('Snapshot', this.snapshot());
    this.later(1500, () => this.openRound());
  }

  // ───── resolución: héroe propio ─────
  actHero(order, events, tilesChanged) {
    const me = this.me;
    if (!this.roundOrder) events.push({ type: 'DEFAULT_GUARD_APPLIED', actorId: ME_ID });
    const [dx, dy] = DIRS[order.direction] ?? [0, 0];
    if (me.hostEnemyId != null) {
      hostOrder(this, order, events, ME_ID);
      return;
    }
    if (order.type !== 'GUARD') me.facing = order.direction;
    if (order.type === 'PUSH') {
      this.push(order, events);
    } else if (order.type === 'MOVE') {
      heroMove(this, order, events, ME_ID);
    } else if (order.type === 'ATTACK') {
      heroAttack(this, order, events, ME_ID);
    } else if (order.type === 'ABILITY') {
      heroAbility(this, order, events, tilesChanged, ME_ID);
    } else if (order.type === 'INTERACT') {
      const x = me.position.x + dx;
      const y = me.position.y + dy;
      const down = this.allies.find((a) => a.state === 'DOWN' && same(a.position, { x, y }));
      if (this.tiles.get(key(x, y)) === 'CHEST') {
        this.tiles.set(key(x, y), 'CHEST_OPENED');
        tilesChanged.push({ x, y, kind: 'CHEST_OPENED' });
        this.chestsOpened += 1;
        me.embers += 2;
        events.push({ type: 'CHEST_OPENED', actorId: ME_ID, position: { x, y } });
        events.push({ type: 'EMBERS_GAINED', actorId: ME_ID, amount: 2 });
      } else if (down) {
        this.revive(down, REVIVE_HP, ME_ID, events);
        me.embers += 2;
        events.push({ type: 'EMBERS_GAINED', actorId: ME_ID, amount: 2 });
      }
    }
  }

  hit(enemy, dmg, events, by = ME_ID) {
    enemy.hp = Math.max(0, enemy.hp - dmg);
    events.push({ type: 'DAMAGED', actorId: by, targetId: `enemy:${enemy.enemyId}`, amount: dmg, _at: enemy.position });
    if (enemy.hp > 0) return;
    this.enemies.delete(enemy.enemyId);
    hostKilled(this, enemy, events);
    const killer = by === ME_ID ? this.me : this.allies.find((a) => a.playerId === by);
    if (killer) killer.embers += 1;
    events.push({ type: 'ENEMY_KILLED', actorId: by, targetId: `enemy:${enemy.enemyId}`, _at: enemy.position });
    events.push({ type: 'EMBERS_GAINED', actorId: by, amount: 1, _at: enemy.position });
    // Golpe final bajo el Faro de la Brasa propia: +1 eco.
    if (by !== ME_ID && this.me.state === 'EMBER_SPIRIT' && this.beacons.some((b) => b.own && manhattan(b, enemy.position) <= 2)) {
      this.echo(1, 'BEACON_KILL', events);
    }
  }

  /** Un héroe golpea a un Renegado aliado: daño completo; a 0 se extingue y quien lo derrotó gana +2. */
  hitRenegade(renegade, dmg, events) {
    renegade.hp = Math.max(0, renegade.hp - dmg);
    events.push({ type: 'DAMAGED', actorId: ME_ID, targetId: renegade.playerId, amount: dmg });
    if (renegade.hp > 0) return;
    events.push({ type: 'RENEGADE_DEFEATED', actorId: ME_ID, targetId: renegade.playerId });
    this.me.embers += 2;
    events.push({ type: 'EMBERS_GAINED', actorId: ME_ID, amount: 2 });
    this.extinguishAlly(renegade, events, 'FRIENDSHIP');
  }

  revive(hero, hp, by, events) {
    hero.state = 'ALIVE';
    hero.hp = hp;
    hero.downRoundsLeft = null;
    events.push({ type: 'REVIVED', actorId: by, targetId: hero.playerId });
  }

  /** Daño a un héroe: primero el escudo de brasas (da ecos a la Brasa propia), luego el escudo normal. */
  damageHero(hero, dmg, sourceId, events) {
    const kindled = this.kindled.get(hero.playerId) ?? 0;
    if (kindled > 0 && dmg > 0) {
      const used = Math.min(kindled, dmg);
      this.kindled.set(hero.playerId, kindled - used);
      dmg -= used;
      if (this.me.state === 'EMBER_SPIRIT') this.echo(used, 'SHIELD_ABSORBED', events);
    }
    if (hero === this.me && this.me.shield > 0 && dmg > 0) {
      const used = Math.min(this.me.shield, dmg);
      this.me.shield -= used;
      dmg -= used;
    }
    if (dmg <= 0) {
      events.push({ type: 'BLOCKED', actorId: sourceId, targetId: hero.playerId, _at: hero.position });
      return 0;
    }
    if (hero.hero === 'AVARO') return miserDamage(this, hero, dmg, events);
    hero.hp = Math.max(hero === this.me && this.scenario.forgiving ? 1 : 0, hero.hp - dmg);
    events.push({ type: 'DAMAGED', actorId: sourceId, targetId: hero.playerId, amount: dmg, _at: hero.position });
    if (hero.hp === 0) {
      hero.state = 'DOWN';
      hero.downRoundsLeft = 3;
      events.push({ type: 'DOWNED', targetId: hero.playerId, _at: hero.position });
    }
    return dmg;
  }

  // ───── resolución: Brasa errante propia ─────
  actSpirit(order, events) {
    const me = this.me;
    const [dx, dy] = DIRS[order.direction] ?? [0, 0];
    if (order.type !== 'GUARD') me.facing = order.direction;
    const firstInLine = (range, find) => {
      for (let i = 1; i <= range; i++) {
        const p = { x: me.position.x + dx * i, y: me.position.y + dy * i };
        if (!inBounds(p.x, p.y)) return null;
        const found = find(p);
        if (found) return found;
      }
      return null;
    };
    switch (order.type) {
      case 'MOVE': {
        // Deriva: hasta 2 casillas, atravesando todo (menos si llega al borde).
        let { x, y } = me.position;
        for (let i = 0; i < 2 && inBounds(x + dx, y + dy); i++) { x += dx; y += dy; }
        if (x !== me.position.x || y !== me.position.y) {
          me.position = { x, y };
          events.push({ type: 'MOVED', actorId: ME_ID, position: clone(me.position) });
        }
        break;
      }
      case 'ABILITY': {
        // Avivar: +1 de escudo al primer aliado activo a 1 o 2 casillas (máx. 2 de brasas).
        me.abilityCooldown = KINDLE_COOLDOWN;
        const ally = firstInLine(2, (p) => this.allies.find((a) => a.state === 'ALIVE' && same(a.position, p)));
        if (ally) {
          const given = this.kindled.get(ally.playerId) ?? 0;
          if (given < 2) this.kindled.set(ally.playerId, given + 1);
          events.push({ type: 'SHIELD_KINDLED', actorId: ME_ID, targetId: ally.playerId });
        } else {
          events.push({ type: 'SHIELD_KINDLED', actorId: ME_ID });
        }
        break;
      }
      case 'INTERACT': {
        // Reavivar: levanta con 1 de vida al caído de la casilla vecina. +3 ecos.
        const p = { x: me.position.x + dx, y: me.position.y + dy };
        const down = this.allies.find((a) => a.state === 'DOWN' && same(a.position, p));
        if (down) {
          this.revive(down, 1, ME_ID, events);
          this.echo(3, 'REKINDLED', events);
        }
        break;
      }
      case 'ATTACK': {
        // Estorbo: el primer enemigo a 1 o 2 casillas retrasa una ronda su golpe. Sin enemigo no gasta el enfriamiento.
        const enemy = firstInLine(2, (p) => [...this.enemies.values()].find((e) => same(e.position, p)));
        if (!enemy) {
          events.push({ type: 'ATTACK_HINDERED', actorId: ME_ID });
          break;
        }
        me.path.hinderCooldown = HINDER_COOLDOWN;
        this.hindered.add(enemy.enemyId);
        events.push({ type: 'ATTACK_HINDERED', actorId: ME_ID, targetId: `enemy:${enemy.enemyId}` });
        if (enemy.telegraph.some((p) => this.heroTargets().some((h) => same(h.position, p)))) this.echo(1, 'ATTACK_HINDERED', events);
        break;
      }
      default: {
        // Faro (también la orden por defecto): ilumina el radio 2 para el grupo.
        this.beacons.push({ ...clone(me.position), own: true });
        events.push({ type: 'BEACON_LIT', actorId: ME_ID, position: clone(me.position) });
      }
    }
  }

  echo(amount, reason, events) {
    const path = this.me.path;
    path.echoes = Math.min(99, path.echoes + amount);
    path.rebirthEmbers = rebirthEmbers(path.savedEmbers, path.echoes);
    events.push({ type: 'ECHO_GAINED', actorId: ME_ID, amount, detail: reason });
  }

  // ───── resolución: Renegado propio ─────
  actRenegade(order, events) {
    const me = this.me;
    const [dx, dy] = DIRS[order.direction] ?? [0, 0];
    if (order.type !== 'GUARD') me.facing = order.direction;
    if (order.type === 'MOVE') {
      heroMove(this, order, events, ME_ID);
    } else if (order.type === 'ATTACK') {
      // El golpe se detiene en el primer cuerpo: un monstruo lo frena sin daño; un héroe recibe el golpe.
      const range = HERO_INFO[me.hero].range ?? 1;
      for (let i = 1; i <= range; i++) {
        const p = { x: me.position.x + dx * i, y: me.position.y + dy * i };
        if (!inBounds(p.x, p.y) || ['WALL', 'BRAZIER'].includes(this.tiles.get(key(p.x, p.y)))) break;
        if ([...this.enemies.values()].some((e) => same(e.position, p))) break;
        const hero = this.allies.find((a) => a.state === 'ALIVE' && same(a.position, p));
        if (hero) { this.renegadeHit(me, hero, events); break; }
      }
      events.push({ type: 'ATTACKED', actorId: ME_ID, position: clone(me.position) });
    } else if (order.type === 'ABILITY') {
      heroAbility(this, order, events, [], ME_ID);
    }
  }

  /** Golpe de un Renegado a un héroe: el primero lo absorbe el escudo de aviso; luego 1 de daño y roba 1 ascua. */
  renegadeHit(renegade, hero, events) {
    const warnKey = `${renegade.playerId}>${hero.playerId}`;
    if (!this.warned.has(warnKey)) {
      this.warned.add(warnKey);
      events.push({ type: 'BLOCKED', actorId: renegade.playerId, targetId: hero.playerId, detail: 'WARNING_SHIELD', _at: hero.position });
      return;
    }
    if (this.damageHero(hero, 1, renegade.playerId, events) <= 0) return;
    if (hero.embers > 0) {
      hero.embers -= 1;
      renegade.embers += 1;
      events.push({ type: 'EMBERS_STOLEN', actorId: renegade.playerId, targetId: hero.playerId, amount: 1, _at: hero.position });
    }
    if (hero.state === 'DOWN') {
      renegade.embers += 2;
      events.push({ type: 'EMBERS_GAINED', actorId: renegade.playerId, amount: 2, _at: renegade.position });
    }
  }

  // ───── resolución: aliados (bots) ─────
  actAllies(events, revealed) {
    for (const ally of this.allies) {
      if (revealed.has(ally.playerId)) continue;
      if (ally.state === 'ALIVE' && ally.position) this.botHero(ally, events);
      else if (ally.state === 'EMBER_SPIRIT' && ally.position) this.botSpirit(ally, events);
      else if (ally.state === 'RENEGADE' && ally.position) this.botRenegade(ally, events);
      if (ally.position) this.allyLast.set(ally.playerId, clone(ally.position));
    }
  }

  stepBot(ally, toward, events) {
    const options = Object.entries(DIRS)
      .map(([d, [dx, dy]]) => ({ d, x: ally.position.x + dx, y: ally.position.y + dy }))
      .filter((o) => !this.blocked(o.x, o.y));
    if (!options.length) return;
    const best = toward
      ? options.sort((a, b) => manhattan(a, toward) - manhattan(b, toward))[0]
      : options[Math.floor(Math.random() * options.length)];
    ally.facing = best.d;
    if (toward && manhattan(best, toward) >= manhattan(ally.position, toward)) return;
    ally.position = { x: best.x, y: best.y };
    events.push({ type: 'MOVED', actorId: ally.playerId, position: clone(ally.position) });
  }

  botHero(ally, events) {
    const me = this.me;
    // Un Renegado propio al lado: lo golpean (daño completo).
    if (me.state === 'RENEGADE' && !this.meAwaiting() && manhattan(ally.position, me.position) === 1) {
      events.push({ type: 'ATTACKED', actorId: ally.playerId, _at: ally.position });
      me.hp = Math.max(0, me.hp - 1);
      events.push({ type: 'DAMAGED', actorId: ally.playerId, targetId: ME_ID, amount: 1 });
      if (me.hp === 0) {
        events.push({ type: 'RENEGADE_DEFEATED', actorId: ally.playerId, targetId: ME_ID });
        ally.embers += 2;
        events.push({ type: 'EMBERS_GAINED', actorId: ally.playerId, amount: 2 });
        this.extinguishMe(events);
      }
      return;
    }
    const enemy = [...this.enemies.values()].find((e) => manhattan(e.position, ally.position) === 1);
    if (enemy && Math.random() < 0.6) {
      events.push({ type: 'ATTACKED', actorId: ally.playerId, _at: ally.position });
      this.hit(enemy, 1, events, ally.playerId);
      return;
    }
    this.stepBot(ally, null, events);
  }

  /** Brasa aliada: deriva hacia el héroe propio, lo aviva y lo calienta; si no, enciende un Faro. */
  botSpirit(ally, events) {
    const me = this.me;
    const cd = this.allyCooldown.get(ally.playerId) ?? 0;
    this.allyCooldown.set(ally.playerId, Math.max(0, cd - 1));
    if (me.state !== 'ALIVE') {
      this.beacons.push(clone(ally.position));
      events.push({ type: 'BEACON_LIT', actorId: ally.playerId, position: clone(ally.position) });
      return;
    }
    const dist = manhattan(ally.position, me.position);
    const inLine = (ally.position.x === me.position.x || ally.position.y === me.position.y) && dist >= 1 && dist <= 2;
    if (inLine && cd === 0 && me.shield < 2) {
      me.shield += 1;
      this.allyCooldown.set(ally.playerId, 2);
      events.push({ type: 'SHIELD_KINDLED', actorId: ally.playerId, targetId: ME_ID });
    } else if (dist > 1) {
      const dx = Math.sign(me.position.x - ally.position.x);
      const dy = Math.sign(me.position.y - ally.position.y);
      const horizontal = Math.abs(me.position.x - ally.position.x) >= Math.abs(me.position.y - ally.position.y);
      const steps = Math.min(2, dist - 1);
      ally.position = horizontal ? { x: ally.position.x + dx * steps, y: ally.position.y } : { x: ally.position.x, y: ally.position.y + dy * steps };
      ally.facing = horizontal ? (dx > 0 ? 'E' : 'W') : (dy > 0 ? 'S' : 'N');
      events.push({ type: 'MOVED', actorId: ally.playerId, position: clone(ally.position) });
    } else {
      this.beacons.push(clone(ally.position));
      events.push({ type: 'BEACON_LIT', actorId: ally.playerId, position: clone(ally.position) });
    }
    // Calidez: cura 1 al héroe propio herido a su lado, cada 3 rondas.
    if (me.hp < me.maxHp && manhattan(ally.position, me.position) <= 1 && (this.warmth.get(ME_ID) ?? 0) <= this.round) {
      me.hp += 1;
      this.warmth.set(ME_ID, this.round + WARMTH_EVERY);
      events.push({ type: 'WARMTH_HEALED', actorId: ally.playerId, targetId: ME_ID });
      events.push({ type: 'HEALED', targetId: ME_ID, amount: 1 });
    }
  }

  /** Renegado aliado: va hacia el héroe propio y lo golpea (el primer golpe, contra el escudo de aviso). */
  botRenegade(ally, events) {
    const me = this.me;
    if (me.state === 'ALIVE' && manhattan(ally.position, me.position) === 1) {
      ally.facing = me.position.x > ally.position.x ? 'E' : me.position.x < ally.position.x ? 'W' : me.position.y > ally.position.y ? 'S' : 'N';
      events.push({ type: 'ATTACKED', actorId: ally.playerId, _at: ally.position });
      this.renegadeHit(ally, me, events);
      return;
    }
    this.stepBot(ally, me.state === 'ALIVE' ? me.position : null, events);
  }

  // ───── resolución: enemigos ─────
  enemiesPhase(events, order) {
    for (const e of this.enemies.values()) {
      // Estorbado por la Brasa: conserva el anuncio, no golpea, no se mueve ni anuncia; golpea la ronda siguiente.
      if (this.hindered.has(e.enemyId)) continue;
      // El huésped de Polizón no anuncia ni actúa por su cuenta.
      if (e.enemyId === this.me.hostEnemyId) continue;
      if (e.correctedBy) correctedStrike(this, e, events);
      for (const h of this.heroTargets()) {
        if (h.hostEnemyId != null || h.hiddenInHost) continue; // a Polizón no lo atacan dentro
        const onSecond = h.secondBody && e.telegraph.some((p) => same(p, h.secondBody));
        if (!onSecond && !e.telegraph.some((p) => same(p, h.position))) continue;
        // GUARDAR anula los golpes de 1.
        if (h === this.me && order.type === 'GUARD') {
          events.push({ type: 'BLOCKED', actorId: `enemy:${e.enemyId}`, targetId: ME_ID });
          continue;
        }
        this.damageHero(h, 1, `enemy:${e.enemyId}`, events);
      }
      if (!this.enemies.has(e.enemyId)) continue;
      const target = this.nearestHero(e.position);
      // El Celador golpea una ronda sí y una no: entre golpes no se mueve, no se gira ni anuncia.
      if (target && e.kind !== 'BALLESTERO' && e.kind !== 'CELADOR') this.stepToward(e, target.position);
      this.updateTelegraph(e);
    }
    this.hindered.clear();
  }

  nearestHero(from) {
    return this.heroTargets().sort((a, b) => manhattan(a.position, from) - manhattan(b.position, from))[0] ?? null;
  }

  // ───── resolución: pasivas y fin de ronda ─────
  passives(events, wasDown) {
    const me = this.me;
    if (me.state === 'EMBER_SPIRIT') {
      // Calidez: cura 1 a cada aliado herido a distancia ≤1, cada 3 rondas por aliado. +1 eco.
      for (const a of this.allies) {
        if (a.state !== 'ALIVE' || !a.position || a.hp >= a.maxHp || manhattan(a.position, me.position) > 1) continue;
        if ((this.warmth.get(a.playerId) ?? 0) > this.round) continue;
        a.hp += 1;
        this.warmth.set(a.playerId, this.round + WARMTH_EVERY);
        events.push({ type: 'WARMTH_HEALED', actorId: ME_ID, targetId: a.playerId });
        events.push({ type: 'HEALED', targetId: a.playerId, amount: 1 });
        this.echo(1, 'WARMTH', events);
      }
      me.abilityCooldown = Math.max(0, me.abilityCooldown - 1);
      me.path.hinderCooldown = Math.max(0, me.path.hinderCooldown - 1);
    }
    // Caído desde antes de esta ronda: tras 3 rondas sin que nadie lo levante, se extingue.
    if (wasDown && me.state === 'DOWN' && me.downRoundsLeft !== null) {
      me.downRoundsLeft = Math.max(0, me.downRoundsLeft - 1);
      if (me.downRoundsLeft === 0) this.extinguishMe(events);
    }
    // Los bots caídos esperan a que los levanten (en la demo no se extinguen solos).
    for (const a of this.allies) if (a.state === 'DOWN' && a.downRoundsLeft > 1) a.downRoundsLeft -= 1;
    // Un aliado que eligió Venganza aparece dos rondas después, cerca (como en la sala del Celador).
    for (const [id, plan] of this.allyPlans) {
      const ally = this.allies.find((a) => a.playerId === id);
      if (!ally || plan.spawn === undefined || plan.spawn > this.round || ally.state !== 'RENEGADE' || ally.position) continue;
      this.spawnRenegade(ally, this.spotNear(me.position, 3), events);
      this.allyPlans.delete(id);
    }
  }

  // ───── caminos: extinción, revelación, aparición ─────
  extinguishMe(events) {
    const me = this.me;
    const saved = me.embers;
    me.state = 'EXTINCT';
    me.hp = 0;
    me.downRoundsLeft = null;
    me.embers = 0;
    me.shield = 0;
    me.abilityCooldown = 0;
    me.hostEnemyId = null;
    me.secondBody = null;
    me.rewindTile = null;
    me.rewindHp = null;
    this.heroes.echoes = this.heroes.echoes.filter((e) => e.ownerId !== ME_ID);
    this.extinctions += 1;
    const offerId = `${PATH_OFFER_PREFIX}${String(this.extinctions).padStart(12, '0')}`;
    me.path = { offerId, choice: null, savedEmbers: saved, echoes: 0, rebirthEmbers: null, hinderCooldown: 0, awaitingSpawn: null, spawnRound: null };
    this.pathOffer = { offerId, choice: null, ack: null, deadline: null };
    // El escudo de aviso se reinicia cada vez que vuelve a extinguirse.
    this.warned = new Set([...this.warned].filter((k) => !k.startsWith(`${ME_ID}>`)));
    events.push({ type: 'EXTINGUISHED', targetId: ME_ID, amount: 0 });
    events.push({ type: 'PATH_OFFERED', actorId: ME_ID, amount: saved });
  }

  /** Aliado que se extingue: elige `path` en la ronda siguiente (los bots no dudan). */
  extinguishAlly(ally, events, path) {
    this.allyLast.set(ally.playerId, clone(ally.position));
    ally.state = 'EXTINCT';
    ally.hp = 0;
    ally.position = null;
    ally.facing = null;
    ally.downRoundsLeft = null;
    events.push({ type: 'EXTINGUISHED', targetId: ally.playerId, amount: 0 });
    this.allyPlans.set(ally.playerId, { reveal: this.round + 1, path });
  }

  /** Inicio de la resolución: se revelan los caminos elegidos (Amistad si no eligió). */
  revealPaths(events) {
    const revealed = new Set();
    const me = this.me;
    const o = this.pathOffer;
    if (o && !o.practice && me.state === 'EXTINCT') {
      const path = o.choice ?? 'FRIENDSHIP';
      if (!o.choice || o.auto) events.push({ type: 'PATH_SELECTED', actorId: ME_ID, detail: `${path}_AUTO` });
      events.push({ type: 'PATH_CHOSEN', actorId: ME_ID, detail: path });
      const saved = me.path.savedEmbers;
      Object.assign(me.path, { offerId: null, choice: null, echoes: 0, hinderCooldown: 0 });
      if (path === 'FRIENDSHIP') {
        me.state = 'EMBER_SPIRIT';
        me.path.rebirthEmbers = rebirthEmbers(saved, 0);
      } else {
        me.state = 'RENEGADE';
        me.embers = Math.ceil(saved / 2);
        me.path.rebirthEmbers = null;
        me.path.awaitingSpawn = this.floor >= 5 ? 'ROUND' : 'NEXT_FLOOR';
        me.path.spawnRound = this.floor >= 5 ? this.round + 2 : null;
      }
      this.pathOffer = null;
      revealed.add(ME_ID);
    }
    for (const [id, plan] of this.allyPlans) {
      const ally = this.allies.find((a) => a.playerId === id);
      if (!ally || ally.state !== 'EXTINCT' || plan.reveal > this.round) continue;
      events.push({ type: 'PATH_CHOSEN', actorId: id, detail: plan.path });
      if (plan.path === 'FRIENDSHIP') {
        ally.state = 'EMBER_SPIRIT';
        ally.position = this.allyLast.get(id) ?? clone(me.position);
        ally.facing = 'S';
        this.allyPlans.delete(id);
      } else {
        ally.state = 'RENEGADE';
        ally.embers = Math.ceil(ally.embers / 2);
        plan.spawn = this.round + 1;
      }
      revealed.add(id);
    }
    return revealed;
  }

  spawnRenegade(h, at, events, firstSpawn = true) {
    h.position = clone(at);
    h.facing = 'S';
    if (firstSpawn) h.hp = Math.ceil(h.maxHp * 0.6);
    if (h === this.me) { h.path.awaitingSpawn = null; h.path.spawnRound = null; }
    // Cada héroe activo gana +1 de escudo.
    if (this.me.state === 'ALIVE' && h !== this.me) this.me.shield += 1;
    events.push({ type: 'RENEGADE_SPAWNED', actorId: h.playerId, position: clone(at) });
  }

  /** Casilla libre a `dist` del punto (si no hay, la más lejana de los héroes). */
  spotNear(from, dist) {
    const spots = [];
    for (let y = 0; y < 9; y++) {
      for (let x = 0; x < 9; x++) {
        if (this.tiles.get(key(x, y)) !== 'FLOOR' || this.blocked(x, y)) continue;
        spots.push({ x, y, d: Math.abs(manhattan({ x, y }, from) - dist) });
      }
    }
    spots.sort((a, b) => a.d - b.d || a.y - b.y || a.x - b.x);
    return spots[0] ?? this.farthestSpot();
  }

  /** Aparición del Renegado: la casilla libre más lejana de los héroes (empate: fila y columna menores). */
  farthestSpot() {
    const heroes = this.heroTargets().map((h) => h.position);
    const from = heroes.length ? heroes : ENTRANCE;
    let best = null;
    for (let y = 0; y < 9; y++) {
      for (let x = 0; x < 9; x++) {
        if (this.tiles.get(key(x, y)) !== 'FLOOR' || this.blocked(x, y)) continue;
        const d = Math.min(...from.map((p) => manhattan(p, { x, y })));
        if (!best || d > best.d) best = { x, y, d };
      }
    }
    return best ? { x: best.x, y: best.y } : { x: 0, y: 0 };
  }

  /** El grupo baja por la escalera: sala nueva, las Brasas renacen y los Renegados aparecen. */
  descend(events) {
    if (this.floor >= 5) return null;
    const me = this.me;
    this.floor += 1;
    const floors = this.scenario.nextFloors ?? DEMO_SCENARIO.nextFloors;
    const plan = floors[(this.floor - 2) % floors.length];
    this.loadLayout(plan.layout);
    this.seen.clear();
    this.beacons = [];
    this.kindled.clear();
    const party = [me, ...this.allies];
    const renegades = party.filter((h) => h.state === 'RENEGADE');
    // Un monstruo menos por Renegado (mínimo 1).
    const roster = plan.enemies.slice(Math.min(renegades.length, plan.enemies.length - 1));
    this.enemies = new Map(roster.map((e) => [e.enemyId, { facing: 'S', phase: null, telegraph: [], maxHp: e.hp, ...clone(e) }]));
    events.push({ type: 'FLOOR_ENTERED', amount: this.floor });
    party.forEach((h, i) => {
      if (h.state === 'ALIVE' || h.state === 'DOWN' || h.state === 'EMBER_SPIRIT') { h.position = clone(ENTRANCE[i]); h.facing = 'N'; }
    });
    // Las Brasas renacen en la entrada con la mitad de su vida.
    for (const h of party) {
      if (h.state !== 'EMBER_SPIRIT') continue;
      h.state = 'ALIVE';
      h.hp = Math.ceil(h.maxHp / 2);
      if (h === me) {
        me.embers = rebirthEmbers(me.path.savedEmbers, me.path.echoes);
        me.path.rebirthEmbers = null;
        me.path.hinderCooldown = 0;
        me.shield = 0;
        me.abilityCooldown = 0;
        if (me.hero === 'HILVAN') placeSecondBody(this, me);
      }
      events.push({ type: 'EMBER_REBORN', actorId: h.playerId, position: clone(h.position), amount: h.hp });
    }
    for (const h of renegades) {
      const first = h === me ? this.meAwaiting() : !h.position;
      h.position = null; // que no se bloquee a sí mismo al buscar casilla
      this.spawnRenegade(h, this.farthestSpot(), events, first);
      this.allyPlans.delete(h.playerId);
    }
    this.enemies.forEach((e) => this.updateTelegraph(e));
    // Al cambiar de sala se pierden las ascuas del suelo, los ecos y la historia de Volver.
    this.heroes.ground = [];
    this.heroes.echoes = [];
    this.heroes.history = [];
    this.heroes.noise = [];
    me.rewindTile = null;
    me.rewindHp = null;
    me.hostEnemyId = null;
    for (const h of party) if (h.hero === 'HILVAN' && h.position) placeSecondBody(this, h);
    return this.floor;
  }

  /** Botones de la demo preparados para esta resolución. */
  applyFlags(events) {
    const me = this.me;
    if (this.flags.extinguish) {
      this.flags.extinguish = false;
      if (me.state === 'ALIVE' || me.state === 'DOWN') {
        if (me.state === 'ALIVE') events.push({ type: 'DOWNED', targetId: ME_ID });
        this.extinguishMe(events);
      } else if (me.state === 'RENEGADE' && !this.meAwaiting()) {
        // beto derrota al Renegado propio: +2 para él y otra elección.
        const beto = this.allies.find((a) => a.playerId === BETO_ID) ?? this.allies[0];
        events.push({ type: 'RENEGADE_DEFEATED', actorId: beto?.playerId, targetId: ME_ID });
        if (beto) { beto.embers += 2; events.push({ type: 'EMBERS_GAINED', actorId: beto.playerId, amount: 2 }); }
        this.extinguishMe(events);
      }
    }
    if (this.flags.renegadeAlly) {
      this.flags.renegadeAlly = false;
      const caro = this.allies.find((a) => a.playerId === CARO_ID);
      if (caro && (caro.state === 'ALIVE' || caro.state === 'DOWN')) {
        if (caro.state === 'ALIVE') events.push({ type: 'DOWNED', targetId: CARO_ID, _at: caro.position });
        this.extinguishAlly(caro, events, 'VENGEANCE');
      }
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
    if (e.enemyId === this.me.hostEnemyId) {
      e.telegraph = tele;
      return;
    }
    if (e.kind === 'CELADOR') {
      if (this.round % 2 === 1) {
        for (const [dx, dy] of Object.values(DIRS)) {
          if (inBounds(e.position.x + dx, e.position.y + dy)) tele.push({ x: e.position.x + dx, y: e.position.y + dy });
        }
      }
    } else if (e.kind === 'BALLESTERO') {
      const [dx, dy] = DIRS[e.facing];
      for (let i = 1; i <= 3; i++) {
        const x = e.position.x + dx * i;
        const y = e.position.y + dy * i;
        if (!inBounds(x, y) || this.tiles.get(key(x, y)) === 'WALL') break;
        tele.push({ x, y });
      }
    } else {
      // Cuerpo a cuerpo: anuncia la casilla adyacente hacia el héroe activo más cercano (no a Brasas ni Renegados).
      const target = this.nearestHero(e.position)?.position;
      let best = null;
      for (const [d, [dx, dy]] of Object.entries(DIRS)) {
        const p = { x: e.position.x + dx, y: e.position.y + dy };
        if (!inBounds(p.x, p.y) || !target) continue;
        if (!best || manhattan(p, target) < manhattan(best.p, target)) best = { d, p };
      }
      if (best) { tele.push(best.p); e.facing = best.d; }
    }
    e.telegraph = tele;
  }

  myPactFulfilled() {
    switch (this.me.pact?.pactId) {
      case 'OPEN_3_CHESTS': return this.chestsOpened >= 3;
      case 'OPEN_1_CHEST': return this.chestsOpened >= 1;
      case 'OUTGOLD_TARGET': return this.me.embers > (this.allies.find((a) => a.playerId === this.me.pact.targetId)?.embers ?? 0);
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
    // La Brasa puntúa con la fórmula del renacer; un extinto que no eligió, con la mitad de lo guardado.
    const myEmbers = me.state === 'EMBER_SPIRIT' ? rebirthEmbers(me.path.savedEmbers, me.path.echoes)
      : me.state === 'EXTINCT' ? Math.ceil((me.path?.savedEmbers ?? 0) / 2) : me.embers;
    const standings = [
      { playerId: me.playerId, displayName: me.displayName, hero: me.hero, finalState: me.state, embers: myEmbers, pact: { ...me.pact, fulfilled: this.myPactFulfilled() } },
      ...this.allies.map((a) => ({ playerId: a.playerId, displayName: a.displayName, hero: a.hero, finalState: a.state, embers: a.embers, pact: this.scenario.allyPacts?.[a.playerId] ?? null })),
    ].sort((a, b) => b.embers - a.embers).map((s, i) => ({ ...s, placement: i + 1 }));
    let winners = [];
    if (outcome === 'WARDEN_DEFEATED') winners = [standings.find((s) => s.finalState !== 'EXTINCT')?.playerId ?? standings[0].playerId];
    if (outcome === 'THRESHOLD_CLAIMED') {
      const up = standings.filter((s) => s.finalState === 'RENEGADE');
      const top = Math.max(...up.map((s) => s.embers));
      winners = up.filter((s) => s.embers === top).map((s) => s.playerId);
    }
    this.t.push('MatchFinished', {
      matchId: DEMO_MATCH_ID, seq: this.seq, outcome, rounds: this.round,
      winners, standings, finishedAt: new Date().toISOString(), betrayals: finalBetrayals(this),
      curses: [...(this.scenario.curses ?? [])],
      awards: this.awards(standings),
      replayUrl: `${location.origin}/api/v1/matches/${DEMO_MATCH_ID}/replay`,
    });
    this.later(300, () => this.t.serverClose('MATCH_FINISHED: la partida terminó.'));
  }

  /** Dos títulos de muestra; en una partida real los calcula el servidor. */
  awards(standings) {
    const out = [];
    const rich = standings[0];
    if (rich) out.push({ awardId: 'RICHEST', playerId: rich.playerId, title: 'El más rico', reason: `Terminó con ${rich.embers} de oro`, value: rich.embers });
    if (this.chestsOpened > 0) out.push({ awardId: 'LOOTER', playerId: ME_ID, title: 'Saqueador', reason: `Abrió ${this.chestsOpened} cofre${this.chestsOpened === 1 ? '' : 's'}`, value: this.chestsOpened });
    return out;
  }

  // ───── botones de los caminos ─────
  requestExtinction() {
    if (this.status !== 'IN_PROGRESS') return 'La partida no está en curso.';
    const s = this.me.state;
    if (s === 'EXTINCT') return 'Ya estás eligiendo camino.';
    if (s === 'EMBER_SPIRIT') return 'El Fantasma no muere: usa «Bajar de piso» para renacer.';
    if (this.meAwaiting()) return 'Tu Traidor aún no apareció: usa «Bajar de piso».';
    this.flags.extinguish = true;
    this.forceResolve();
    return s === 'RENEGADE' ? 'beto derrota a tu Traidor: vuelves a elegir camino.' : 'Tu héroe muere al resolver la ronda: elegirás camino.';
  }

  requestRenegadeAlly() {
    if (this.status !== 'IN_PROGRESS') return 'La partida no está en curso.';
    const caro = this.allies.find((a) => a.playerId === CARO_ID);
    if (!caro) return 'No hay aliados en este escenario.';
    if (caro.state === 'RENEGADE') return 'caro ya es Traidor.';
    if (caro.state !== 'ALIVE' && caro.state !== 'DOWN') return 'caro ya recorre otro camino.';
    this.flags.renegadeAlly = true;
    this.forceResolve();
    return 'caro muere, elige Vengarse y aparecerá cerca de ti como Traidor.';
  }

  requestDescend() {
    if (this.status !== 'IN_PROGRESS') return 'La partida no está en curso.';
    if (this.floor >= 5) return 'Ya están en la última sala.';
    this.flags.descend = true;
    this.forceResolve();
    return 'El grupo baja por la escalera al resolver la ronda.';
  }
}

/** Implementa la misma interfaz que RealTransport (hub.js). */
export class DemoTransport {
  constructor(scenario = demoScenario()) {
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

  /** Fin de partida: si eres un Renegado en pie, «El Umbral reclama»; si no, cae el Celador. */
  simulateFinish() {
    const s = this.server;
    s.finish(s.me.state === 'RENEGADE' && !s.meAwaiting() ? 'THRESHOLD_CLAIMED' : 'WARDEN_DEFEATED');
  }

  simulateExtinction() { return this.server.requestExtinction(); }

  simulateRenegadeAlly() { return this.server.requestRenegadeAlly(); }

  simulateDescend() { return this.server.requestDescend(); }

  dispose() { this.server.dispose(); }
}
