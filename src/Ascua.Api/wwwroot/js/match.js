// Pantalla de partida: estado de la vista, mensajes del hub, órdenes, reconexión y fin.
// Funciona igual con el transporte real (hub.js) y con el de la demo (demo.js).
import { $, el, setText, showScreen, toast } from './ui.js';
import { BoardRenderer } from './board.js';
import { logMessage, setLastSeq } from './log.js';
import { NO_RECONNECT } from './hub.js';
import { uuidv7 } from './uuid.js';
import { emit, on } from './bus.js';
import { BLESSING_ICONS, heroAvatar, heroInfo } from './heroes.js';
import { settingsOpen } from './settings-panel.js';
import { loadCatalog } from './catalog.js';
import * as api from './api.js';
import { shiftFromEvent, shiftText, shiftedPos } from './board-heroes.js';
import {
  BLESSINGS, BLESSING_COSTS, BLESSING_EFFECTS, CURSES, DIRECTIONS, ECHO_REASONS, EMBER_ORDERS, ENEMIES, ERROR_CODES, EVENTS, HEROES, HERO_STATES,
  MATCH_STATUS, ORDER_TYPES, OUTCOMES, PATHS, PHASES, ROOM_TYPES, SIGNALS, label,
} from './labels.js';

const KEY_DIRS = { ArrowUp: 'N', ArrowDown: 'S', ArrowLeft: 'W', ArrowRight: 'E' };
const KEY_SIGNALS = { 1: 'HERE', 2: 'DANGER', 3: 'HELP', 4: 'MINE' };
const KEY_ACTIONS = { a: 'ATTACK', q: 'ABILITY', e: 'INTERACT', f: 'PUSH' };
/** Antes del cierre se envía lo planeado, para que llegue a tiempo. */
const AUTO_SEND_MS = 900;
const REMATCH_POLL_MS = 3000;
const SIGNAL_INTERVAL_MS = 3000;
const SIGNAL_FEED_MAX = 3;
const RETRYABLE = new Set(['RATE_LIMITED', 'INTERNAL_ERROR']);
const FATAL_JOIN = new Set(['NOT_PARTICIPANT', 'MATCH_NOT_FOUND', 'MATCH_NOT_ACTIVE', 'ALREADY_IN_ANOTHER_MATCH']);

/** Ícono y tono de cada evento en el registro de la ronda ('' = neutro). */
const EVENT_STYLE = {
  MOVED: ['👣', ''], BOUNCED: ['↩', ''], ATTACKED: ['⚔', ''], DAMAGED: ['💥', ''], BLOCKED: ['⛨', 'info'],
  HEALED: ['✚', 'good'], DOWNED: ['☠', 'bad'], REVIVED: ['✋', 'good'], EXTINGUISHED: ['🕯', 'bad'],
  ENEMY_KILLED: ['💀', 'gold'], ENEMY_SPAWNED: ['👁', 'bad'], CHEST_OPENED: ['📦', 'gold'], EMBERS_GAINED: ['✦', 'gold'],
  EMBERS_STOLEN: ['✦', 'bad'], BLESSING_GAINED: ['✧', 'gold'], TRAP_TRIGGERED: ['⚠', 'bad'], FIRE_STARTED: ['🔥', 'bad'],
  FROZEN: ['❄', 'info'], BOSS_PHASE_CHANGED: ['👑', 'bad'], FLOOR_ENTERED: ['⇣', 'info'], DEFAULT_GUARD_APPLIED: ['⛨', ''],
  ABILITY_USED: ['✺', ''], GUARDED: ['⛨', ''],
  // Caminos tras la extinción.
  PATH_OFFERED: ['🕯', 'bad'], PATH_SELECTED: ['✔', 'info'], PATH_CHOSEN: ['⚖', 'info'], EMBER_REBORN: ['↺', 'ember'],
  RENEGADE_SPAWNED: ['☠', 'renegade'], RENEGADE_DEFEATED: ['⚔', 'gold'], ECHO_GAINED: ['❖', 'ember'],
  SHIELD_KINDLED: ['❂', 'ember'], ATTACK_HINDERED: ['⧖', 'ember'], BEACON_LIT: ['☼', 'ember'], WARMTH_HEALED: ['♨', 'ember'],
  // Los nueve héroes.
  ECHO_FIRED: ['❖', 'info'], ATTACK_CORRECTED: ['✎', 'info'], BETRAYAL_RECORDED: ['🗡', 'bad'], BETRAYAL_REVEALED: ['🗡', 'bad'],
  HOST_BOARDED: ['◉', 'good'], HOST_EXITED: ['◉', ''], HOST_DRAINED: ['◉', 'good'], WARDEN_TURNED: ['↻', 'info'], SHOUTED: ['📢', 'info'],
  DEBT_CHANGED: ['⚖', 'gold'], EMBERS_DROPPED: ['✦', 'bad'], EMBERS_PICKED_UP: ['✦', 'gold'], EMBERS_LOST: ['✦', 'bad'], ENEMY_BRIBED: ['✦', 'gold'],
  ROOM_SHIFTED: ['⇄', 'info'], BELL_RUNG: ['🔔', 'info'], BELL_EXPIRED: ['🔔', ''],
  PUSHED: ['🖐', 'info'], ROOM_FLOODED: ['🌊', 'bad'], CHILLED: ['❄', 'bad'],
};

/** Sucesos que se resaltan en el registro: cambian la partida. */
const KEY_EVENTS = new Set(['DOWNED', 'EXTINGUISHED', 'ENEMY_KILLED', 'RENEGADE_DEFEATED', 'PUSHED', 'BOSS_PHASE_CHANGED', 'BETRAYAL_RECORDED']);

/** Un golpe sin autor en la fase de acciones no puede ser una trampa: alguien saboteó. */
export const isSabotageHit = (ev) => ev.type === 'DAMAGED' && !ev.actorId && ev.phase === 'ACTIONS' && Boolean(ev.targetId) && !String(ev.targetId).startsWith('enemy:');

/** Por qué cambió una deuda (DEBT_CHANGED.detail). */
const DEBT_REASONS = {
  LOAN: 'préstamo', INTEREST: 'interés', PAID: 'pago', EXTINCTION: 'cobro por muerte', MATCH_END: 'cobro final', CANCELLED: 'cancelada',
};

/** Habilidades a las que la dirección no les importa. */
const NO_AIM = new Set(['ANTEAYER', 'HILVAN', 'ESCUCHA', 'CAMPANERO']);

/** Qué resaltar y qué decir mientras se elige el objetivo de cada acción. */
const PICK_TEXT = {
  ATTACK: 'Elige a quién atacar: clic en una casilla resaltada.',
  ABILITY: 'Elige hacia dónde: clic en una casilla resaltada.',
  INTERACT: 'Elige qué usar: clic en una casilla resaltada (cofre, altar, aliado derribado).',
  PUSH: 'Elige a quién empujar: clic en la casilla de al lado.',
};
const EMBER_PICK_TEXT = {
  ATTACK: 'Frenar: clic en la casilla del enemigo (hasta 2).',
  ABILITY: 'Escudo: clic en la casilla del aliado (hasta 2).',
  INTERACT: 'Revivir: clic en la casilla del derribado.',
};

/** Nombres e íconos originales de los botones de orden (se restauran al dejar de ser Brasa). */
const HERO_ORDERS = {
  MOVE: { name: 'Paso', icon: '➜' }, ATTACK: { name: 'Atacar', icon: '⚔' }, ABILITY: { name: 'Habilidad', icon: '✺' },
  INTERACT: { name: 'Usar', icon: '✋' }, GUARD: { name: 'Defender', icon: '⛨' },
  PUSH: { name: 'Empujar', icon: '🖐' },
};

const PATH_DEADLINE_MS = 15000;
/** Fórmula del renacer de la Brasa: 25% de las ascuas guardadas, +8% por eco, tope 85%. */
const rebirthPct = (echoes) => Math.min(85, 25 + 8 * Math.max(0, echoes));

/** Renegado que todavía no apareció (no tiene casilla). */
const awaitingSpawn = (me) => me?.state === 'RENEGADE' && Boolean(me.path?.awaitingSpawn);
/** Puede dar órdenes en la ronda: héroe vivo, Brasa o Renegado presente. */
const canAct = (me) => me?.state === 'ALIVE' || me?.state === 'EMBER_SPIRIT' || (me?.state === 'RENEGADE' && !awaitingSpawn(me));
/** Clase de estilo del camino de un portador ('' si no recorre ninguno). */
const pathClass = (state) => ({ EMBER_SPIRIT: 'ember', RENEGADE: 'renegade', EXTINCT: 'choosing' }[state] ?? '');

/** Reinicia una animación CSS de clase (quitar, forzar reflow y volver a poner). */
function replay(node, cls) {
  node.classList.remove(cls);
  void node.offsetWidth;
  node.classList.add(cls);
}

let active = null; // partida en curso (una a la vez)

/**
 * @param {object} options
 * @param {object} options.transport   RealTransport o DemoTransport.
 * @param {() => void} options.onExit  Volver al lobby.
 */
export function startMatch({ transport, onExit, onRematch }) {
  active?.dispose();
  active = new MatchController(transport, onExit, onRematch);
  active.start();
  return active;
}

/** Corta la partida sin volver al lobby (p. ej. al perder la sesión). */
export function stopMatch() {
  active?.dispose();
  active = null;
}

class MatchController {
  constructor(transport, onExit, onRematch) {
    this.transport = transport;
    this.matchId = transport.matchId;
    this.onExit = onExit;
    this.onRematch = onRematch;
    this.toast = toast;
    this.rematchTimer = 0;
    this.board = new BoardRenderer($('board'));
    this.disposed = false;
    this.finished = null;
    this.reconnectAttempt = 0;
    this.reconnectTimer = 0;
    this.drainDelayMs = null;
    this.selectedMode = 'MOVE';
    this.armedMode = null; // acción elegida en la fase 2, a la espera de su objetivo
    this.plannedStep = null; // paso planeado (null = quedarse)
    this.plannedAction = null; // acción planeada { type, direction }
    this.phase = 'MOVE'; // MOVE (1 Moverte) | ACT (2 Actuar)
    this.order = null; // { payload, status: sending|waiting|acked|error, ack, error }
    this.draft = null;
    this.pactOffer = null; // oferta de misión { offerId, options, deadline, sending }
    this.pathOffer = null; // oferta de camino abierta { offerId, deadlineLocal, savedEmbers, …, sending }
    this.signalMark = null; // casilla marcada para la próxima señal
    this.signalReadyAt = 0;
    this.signalFeed = [];
    this.resetView();

    this.onKey = (e) => this.handleKey(e);
    this.tick = setInterval(() => this.renderTimer(), 200);
    this.cleanups = [];
  }

  resetView() {
    this.playerId = null;
    this.seq = null;
    this.status = null;
    this.round = 0;
    this.floor = null;
    this.room = { type: 'NORMAL', floodedRows: 0 };
    this.curses = [];
    this.me = null;
    this.allies = [];
    this.enemies = new Map();
    this.tiles = new Map();
    this.hiddenTelegraphs = [];
    this.echoes = [];
    this.groundEmbers = [];
    this.noiseTiles = [];
    this.bell = null; // Repique sonando { ringerId, deadlineLocal, closed }
    this.lastWord = null; // órdenes que oye el Campanero en esta ronda
    this.nextDeadline = null; // plazo propio que trajo la resolución, para la ronda que abre
    this.deadlineLocal = null;
    this.durationMs = 20000;
    this.decidedCount = 0;
    this.expectedPlayers = 0;
    this.events = [];
    this.clockOffset = 0; // hora del servidor − hora local (ms)
    // Lo último que se pintó, para animar solo lo que cambia.
    this.shown = { hp: null, embers: null, echoes: null, blessings: null, pact: null, events: null, hero: null, orders: null, decided: new Set(), legend: null };
  }

  // ───────────── ciclo de vida ─────────────

  start() {
    showScreen('match');
    const demo = this.transport.protocolName === 'demo';
    $('btn-demo-drop').hidden = !demo;
    $('btn-demo-finish').hidden = !demo;
    $('demo-paths').hidden = !demo;
    this.bindUi();
    if (this.transport.protocolName !== 'demo' && this.transport.protocolName !== 'tutorial') {
      // Con el catálogo real se rehacen los textos de una oferta que ya estuviera abierta.
      loadCatalog().then((changed) => { if (changed && !this.disposed && this.draft) this.renderDraftOptions(); });
    }
    for (const type of ['RoundOpened', 'PlayerDecided', 'RoundResolved', 'Snapshot', 'DraftOffer', 'PactOffer', 'PathOffer',
      'LastWord', 'SignalReceived', 'PlayerConnectionChanged', 'MatchFinished', 'ServerDraining', 'Error']) {
      this.transport.on(type, (payload) => {
        if (this.disposed) return;
        logMessage('in', type, payload);
        this[`on${type}`](payload);
        this.render();
      });
    }
    this.transport.onClose((reason) => this.handleClose(reason));
    emit('match:start', { mode: demo ? 'demo' : this.transport.protocolName === 'tutorial' ? 'tutorial' : 'online' });
    this.render();
    this.connect();
  }

  dispose() {
    this.disposed = true;
    clearInterval(this.tick);
    clearTimeout(this.reconnectTimer);
    clearTimeout(this.rematchTimer);
    document.removeEventListener('keydown', this.onKey);
    this.cleanups.forEach((fn) => fn());
    this.transport.stop();
    this.transport.dispose?.();
    $('draft').hidden = true;
    $('path').hidden = true;
    emit('match:stop');
  }

  exit() {
    this.dispose();
    if (active === this) active = null;
    this.onExit();
  }

  bindUi() {
    document.addEventListener('keydown', this.onKey);
    const listen = (node, type, fn) => {
      node.addEventListener(type, fn);
      this.cleanups.push(() => node.removeEventListener(type, fn));
    };
    // Fase 1: clic en una casilla vecina. Fase 2: un botón y luego el objetivo en el tablero. Listo envía.
    document.querySelectorAll('.phase-btn').forEach((btn) => listen(btn, 'click', () => this.goPhase(btn.dataset.phase)));
    document.querySelectorAll('.act').forEach((btn) => listen(btn, 'click', () => this.pickAction(btn.dataset.act)));
    listen($('btn-ready'), 'click', () => this.ready());
    listen($('btn-plan-reset'), 'click', () => this.resetPlan(true));
    listen($('btn-leave-match'), 'click', () => this.leave());
    listen($('btn-demo-drop'), 'click', () => toast(this.transport.simulateDrop()));
    listen($('btn-demo-finish'), 'click', () => this.transport.simulateFinish());
    listen($('btn-demo-extinct'), 'click', () => toast(this.transport.simulateExtinction()));
    listen($('btn-demo-renegade'), 'click', () => toast(this.transport.simulateRenegadeAlly()));
    listen($('btn-demo-floor'), 'click', () => toast(this.transport.simulateDescend()));
    document.querySelectorAll('.signal').forEach((btn) => listen(btn, 'click', () => this.sendSignal(btn.dataset.signal)));
    this.cleanups.push(on('board:tile', (p) => this.markTile(p)));
    document.querySelectorAll('.path-card').forEach((btn) => listen(btn, 'click', () => this.choosePath(btn.dataset.path)));
    // En el diálogo de caminos las flechas pasan de una carta a la otra (Enter o Espacio eligen).
    listen($('path'), 'keydown', (e) => {
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
      e.preventDefault();
      document.querySelector(`.path-card[data-path="${e.key === 'ArrowLeft' ? 'FRIENDSHIP' : 'VENGEANCE'}"]`)?.focus();
    });
  }

  // ───────────── conexión ─────────────

  setConn(text, kind = '') {
    const badge = $('conn-state');
    badge.textContent = `${text} (${this.transport.protocolName})`;
    badge.className = `badge ${kind}`;
    emit('conn:state', { text, kind });
  }

  async connect() {
    if (this.disposed || this.finished) return;
    const reconnecting = this.reconnectAttempt > 0 || this.seq !== null;
    this.setConn(reconnecting ? `reconectando (intento ${this.reconnectAttempt + 1})` : 'conectando', 'warn');
    logMessage('sys', reconnecting ? 'Reconnecting' : 'Connecting', { attempt: this.reconnectAttempt + 1 });
    try {
      await this.transport.connect();
      logMessage('sys', 'Connected', { protocol: this.transport.protocolName });
      const ack = await this.invoke('JoinMatch', { matchId: this.matchId });
      if (ack.error) {
        if (FATAL_JOIN.has(ack.error.code)) {
          toast(`No se pudo entrar: ${label(ERROR_CODES, ack.error.code)}`, 'bad', 6000);
          this.exit();
          return;
        }
        throw new Error(ack.error.message);
      }
      this.playerId = ack.playerId;
      this.applySnapshot(ack.snapshot);
      if (reconnecting) {
        // Al reconectar se pide la vista explícitamente: puede haberse perdido un RoundResolved.
        await this.requestSnapshot();
      }
      this.reconnectAttempt = 0;
      this.setConn('conectado', 'ok');
      await this.resendPendingOrder();
    } catch (err) {
      if (this.disposed) return;
      logMessage('sys', 'ConnectFailed', { message: String(err?.message ?? err) });
      // Errores HTTP del ticket (401 tras intentar renovar, 403, 404): reintentar no sirve.
      if (err?.status === 401 || err?.status === 403 || err?.status === 404) {
        toast(`No se pudo obtener el ticket: ${err.message}`, 'bad', 6000);
        this.exit();
        return;
      }
      this.scheduleReconnect();
    }
    this.render();
  }

  scheduleReconnect(minDelayMs = 0) {
    if (this.disposed || this.finished) return;
    // onclose y el catch de connect() pueden pedirlo a la vez: un solo reintento pendiente.
    if (this.reconnectTimer && minDelayMs === 0) return;
    clearTimeout(this.reconnectTimer);
    // Backoff exponencial 1, 2, 4… máx. 30 s, con jitter (contrato §2, INTERNAL_ERROR).
    const base = Math.min(30000, 1000 * 2 ** this.reconnectAttempt);
    const delay = Math.max(minDelayMs, base / 2 + Math.random() * base / 2);
    this.reconnectAttempt += 1;
    this.setConn(`sin conexión; reintento en ${Math.ceil(delay / 1000)} s`, 'bad');
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = 0;
      this.connect();
    }, delay);
    this.render();
  }

  handleClose(reason) {
    if (this.disposed) return;
    const code = reason?.code ?? null;
    logMessage('sys', 'Closed', { code, message: reason?.message ?? '' });
    if (code === 'MATCH_FINISHED' || this.finished) {
      this.setConn('cerrado: partida terminada', '');
      if (!this.finished) this.exit();
      return;
    }
    if (NO_RECONNECT.has(code)) {
      const texts = {
        SESSION_REPLACED: 'Abriste esta partida en otra pestaña; esta conexión se cerró.',
        KICKED_BY_ADMIN: 'Un administrador te expulsó de la partida.',
        UNSUPPORTED_PROTOCOL_VERSION: 'Versión de protocolo no soportada; actualiza el cliente.',
      };
      this.setConn(`cerrado: ${code}`, 'bad');
      toast(texts[code] ?? code, 'bad', 8000);
      return;
    }
    let minDelay = 0;
    if (code === 'RATE_LIMITED') minDelay = 60000;
    if (code === 'SERVER_DRAINING' && this.drainDelayMs !== null) minDelay = this.drainDelayMs;
    this.drainDelayMs = null;
    if (this.order && this.order.status === 'sending') this.order.status = 'waiting';
    this.scheduleReconnect(minDelay);
  }

  async invoke(method, payload) {
    logMessage('out', method, payload);
    const result = await this.transport.invoke(method, payload);
    logMessage('in', result?.error ? 'InvocationError' : `${method}Ack`, result);
    return result;
  }

  async requestSnapshot() {
    try {
      const snap = await this.invoke('GetSnapshot', { matchId: this.matchId });
      if (snap?.error) {
        toast(`GetSnapshot: ${label(ERROR_CODES, snap.error.code)}`, 'bad');
        return;
      }
      this.applySnapshot(snap);
      this.render();
    } catch (err) {
      logMessage('sys', 'GetSnapshotFailed', { message: String(err?.message ?? err) });
    }
  }

  // ───────────── vista ─────────────

  applySnapshot(snap) {
    const v = snap.view;
    this.seq = snap.seq;
    this.status = v.status;
    this.round = v.round;
    const previousFloor = this.floor;
    const before = previousFloor !== null && previousFloor !== v.floor ? this.entities() : null;
    this.floor = v.floor;
    this.room = v.room ?? this.room;
    this.curses = v.curses ?? this.curses;
    this.me = v.me;
    this.allies = v.allies ?? [];
    this.enemies = new Map((v.enemies ?? []).map((e) => [e.enemyId, e]));
    this.tiles = new Map((v.tiles ?? []).map((t) => [`${t.x},${t.y}`, t.kind]));
    this.hiddenTelegraphs = v.hiddenTelegraphs ?? [];
    this.echoes = v.echoes ?? [];
    this.groundEmbers = v.groundEmbers ?? [];
    this.noiseTiles = v.noiseTiles ?? [];
    // El plazo se traduce al reloj local con el desfase que informa el servidor.
    const offset = Date.parse(snap.serverTime) - Date.now();
    this.clockOffset = Number.isFinite(offset) ? offset : 0;
    this.deadlineLocal = v.roundDeadline ? Date.parse(v.roundDeadline) - this.clockOffset : null;
    this.setBell(v.bell);
    this.decidedCount = [this.me, ...this.allies].filter((h) => h?.decided).length;
    // Dan orden los héroes vivos, las Brasas y los Renegados presentes (un aliado Renegado se cuenta si se le ve).
    this.expectedPlayers = (canAct(this.me) ? 1 : 0) + this.allies.filter((a) => a.state === 'ALIVE' || a.state === 'EMBER_SPIRIT'
      || (a.state === 'RENEGADE' && a.position)).length;
    if (this.order && this.order.payload.round !== this.round) this.order = null;
    setLastSeq(this.seq);
    this.syncPathOffer();
    this.renderPactOptions();
    if (before) {
      // Se cambió de sala al cerrarse el interludio: el tablero hace la transición.
      emit('floor:changed', { floor: v.floor, room: this.room });
      emit('round:resolved', { round: this.round, events: [], before, after: this.entities(), myId: this.playerId });
    }
  }

  /**
   * La vista manda: con `me.path.offerId` y sin elección hay que elegir (al reconectar puede no llegar el
   * PathOffer antes que la vista, así que se abre con los números que se pueden calcular); si ya no hay
   * oferta o ya se eligió, el diálogo se cierra.
   */
  syncPathOffer() {
    const path = this.me?.path;
    const open = Boolean(path?.offerId) && !path.choice && this.me.state === 'EXTINCT' && this.status === 'IN_PROGRESS';
    if (!open) {
      if (this.pathOffer && path?.choice && !this.pathOffer.sending) {
        // El plazo solo puede tomar Amistad; otro camino lo eligió este jugador desde otra conexión.
        toast(path.choice === 'FRIENDSHIP' ? `Venció el plazo: tomaste el ${label(PATHS, path.choice)}.` : `Quedó registrado el ${label(PATHS, path.choice)}.`, 'info', 5000);
      }
      if (this.pathOffer) this.closePathDialog();
      return;
    }
    if (this.pathOffer?.offerId === path.offerId) return;
    const saved = path.savedEmbers ?? 0;
    const deadline = this.deadlineLocal ?? Date.now() + PATH_DEADLINE_MS;
    this.openPathDialog({
      offerId: path.offerId,
      savedEmbers: saved,
      vengeanceEmbers: Math.ceil(saved / 2),
      friendshipEmbersMin: Math.ceil(saved * 0.25),
      friendshipEmbersMax: Math.ceil(saved * 0.85),
      wardenRoom: (this.floor ?? 1) >= 5,
      deadlineLocal: Math.min(deadline, Date.now() + PATH_DEADLINE_MS),
    });
  }

  onRoundOpened(m) {
    if (m.round < this.round) return;
    this.status = 'IN_PROGRESS';
    this.round = m.round;
    this.resetPlan(false);
    // Se usa la duración (no la hora absoluta) para no depender del reloj del cliente. Con el Repique, el plazo de cada
    // jugador (roundDeadline; si no llegó, bellDeadline salvo para quien lo tocó) es menor que el de la ronda.
    const ringing = Boolean(m.bellDeadline);
    const mine = this.nextDeadline ?? (ringing && m.bellRingerId !== this.playerId ? m.bellDeadline : m.deadline);
    const short = (iso) => Math.max(0, Date.parse(m.deadline) - Date.parse(iso)) || 0;
    this.durationMs = Math.max(1000, m.durationMs - short(mine));
    this.nextDeadline = null;
    this.deadlineLocal = Date.now() + this.durationMs;
    this.bell = ringing ? { ringerId: m.bellRingerId, deadlineLocal: Date.now() + m.durationMs - short(m.bellDeadline), closed: false } : null;
    this.lastWord = null;
    this.decidedCount = 0;
    this.expectedPlayers = m.expectedPlayers;
    if (this.me) { this.me.decided = false; this.me.pendingOrder = null; }
    this.allies.forEach((a) => { a.decided = false; });
    if (this.order && this.order.payload.round !== m.round) this.order = null;
    this.lowTimeRound = null;
    // La elección de camino vence 15 s después de abrir la ronda (nunca después de su cierre).
    if (this.pathOffer && !this.pathOffer.precise) this.pathOffer.deadlineLocal = Date.now() + Math.min(PATH_DEADLINE_MS, m.durationMs);
    emit('round:opened', { round: m.round, durationMs: m.durationMs });
  }

  onPlayerDecided(m) {
    if (m.round !== this.round) return;
    this.decidedCount = m.decidedCount;
    this.expectedPlayers = m.expectedPlayers;
    if (this.me && m.playerId === this.playerId) this.me.decided = true;
    const ally = this.allies.find((a) => a.playerId === m.playerId);
    if (ally) ally.decided = true;
    emit('player:decided', { playerId: m.playerId, isMe: m.playerId === this.playerId, decidedCount: m.decidedCount, expectedPlayers: m.expectedPlayers });
  }

  onRoundResolved(m) {
    if (this.seq !== m.previousSeq) {
      // Hueco de secuencia: se descarta el delta y se pide la vista completa (contrato §4).
      logMessage('sys', 'SeqGap', { expected: this.seq, previousSeq: m.previousSeq });
      this.requestSnapshot();
      return;
    }
    const d = m.delta;
    const before = this.entities();
    this.status = d.status;
    if (d.floorChanged) {
      this.floor = d.floorChanged;
      this.tiles = new Map();
      this.enemies = new Map();
    }
    // Corrimiento: lo recordado fuera de la visión se corre aquí; lo visible llega abajo en tilesChanged.
    for (const ev of d.events ?? []) if (ev.type === 'ROOM_SHIFTED') this.shiftKnownTiles(ev);
    this.me = d.me;
    for (const ally of d.alliesChanged ?? []) {
      const i = this.allies.findIndex((a) => a.playerId === ally.playerId);
      if (i >= 0) this.allies[i] = ally; else this.allies.push(ally);
    }
    for (const enemy of d.enemiesUpserted ?? []) this.enemies.set(enemy.enemyId, enemy);
    for (const id of d.enemiesRemoved ?? []) this.enemies.delete(id);
    for (const t of d.tilesChanged ?? []) this.tiles.set(`${t.x},${t.y}`, t.kind);
    // Estas listas son completas y, si faltan, no cambiaron (contrato: PlayerViewDelta).
    this.hiddenTelegraphs = d.hiddenTelegraphs ?? this.hiddenTelegraphs;
    this.echoes = d.echoes ?? this.echoes;
    this.groundEmbers = d.groundEmbers ?? this.groundEmbers;
    this.noiseTiles = d.noiseTiles ?? this.noiseTiles;
    this.lastWord = null;
    this.bell = null;
    this.resetPlan(false);
    this.nextDeadline = d.roundDeadline ?? null;
    this.events = d.events ?? [];
    const flood = this.events.find((ev) => ev.type === 'ROOM_FLOODED');
    if (flood) this.room = { ...this.room, floodedRows: flood.amount ?? this.room.floodedRows };
    for (const ev of this.events) {
      if (ev.type === 'BETRAYAL_RECORDED') toast(`Nadie lo sabe todavía: le costaste ${ev.amount ?? '?'} de vida a ${this.nameOf(ev.targetId)}. Se sabrá al final.`, 'info', 5000);
      if (ev.type === 'CHILLED') toast(`Llega el frío: ${ev.amount ?? 1} de daño a cada uno. Terminen ya.`, 'bad', 4000);
      if (isSabotageHit(ev)) emit('sabotage:seen', { targetId: ev.targetId, onMe: ev.targetId === this.playerId });
    }
    this.seq = m.seq;
    this.deadlineLocal = null;
    setLastSeq(this.seq);
    if (d.floorChanged) {
      const entered = this.events.find((ev) => ev.type === 'FLOOR_ENTERED');
      const type = entered?.detail ?? 'NORMAL';
      const known = ROOM_TYPES[type] ?? {};
      this.room = { ...this.room, type, name: known.name ?? type, effect: known.effect ?? '', floodedRows: 0 };
      emit('floor:changed', { floor: d.floorChanged, room: this.room });
    }
    emit('round:resolved', { round: m.round, events: this.events, before, after: this.entities(), myId: this.playerId });
    this.syncPathOffer();
  }

  /** Rota la línea corrida en las casillas recordadas. Si no se sabía qué había en el origen, se deja lo que había. */
  shiftKnownTiles(ev) {
    const shift = shiftFromEvent(ev);
    if (!shift) return;
    const next = new Map(this.tiles);
    for (let i = 0; i < 9; i++) {
      const from = shift.axis === 'row' ? { x: i, y: shift.index } : { x: shift.index, y: i };
      const to = shiftedPos(shift, from);
      const kind = this.tiles.get(`${from.x},${from.y}`);
      if (kind !== undefined) next.set(`${to.x},${to.y}`, kind);
    }
    this.tiles = next;
  }

  /** Repique según la vista: se traduce el plazo público al reloj local. */
  setBell(bell) {
    const wasOpen = this.bell && !this.bell.closed;
    if (!bell) {
      this.bell = null;
      return;
    }
    const deadlineLocal = bell.deadline ? Date.parse(bell.deadline) - this.clockOffset : this.bell?.deadlineLocal ?? null;
    this.bell = { ringerId: bell.ringerId, deadlineLocal, closed: Boolean(bell.closed) };
    if (wasOpen && bell.closed && bell.ringerId !== this.playerId && canAct(this.me) && !this.me?.decided) {
      toast('Se acabó tu plazo del Repique: defiendes esta ronda.', 'info', 5000);
    }
  }

  /** Nombre de un jugador para los textos. */
  nameOf(id) {
    if (id === this.playerId) return this.me?.displayName ?? 'Tú';
    return this.allies.find((a) => a.playerId === id)?.displayName ?? 'Alguien';
  }

  heroOf(id) {
    if (id === this.playerId) return this.me?.hero ?? null;
    return this.allies.find((a) => a.playerId === id)?.hero ?? null;
  }


  // ───────────── señales y Último en hablar ─────────────

  onLastWord(m) {
    if (m.round !== this.round) return;
    this.lastWord = m;
    emit('lastword:heard', { typesOnly: Boolean(m.typesOnly) });
  }

  onSignalReceived(m) {
    this.signalFeed = [{ ...m, shownAt: Date.now() }, ...this.signalFeed].slice(0, SIGNAL_FEED_MAX);
    emit('signal:received', m);
  }

  markTile(p) {
    if (this.planTile(p)) return;
    const same = this.signalMark?.x === p.x && this.signalMark?.y === p.y;
    this.signalMark = same ? null : p;
    this.render();
  }

  async sendSignal(kind) {
    if (this.status !== 'IN_PROGRESS' || !this.transport.connected) return;
    if (Date.now() < this.signalReadyAt) return;
    this.signalReadyAt = Date.now() + SIGNAL_INTERVAL_MS;
    const payload = { matchId: this.matchId, kind };
    if (this.signalMark) payload.position = this.signalMark;
    this.signalMark = null;
    this.render();
    try {
      const res = await this.invoke('SendSignal', payload);
      if (res?.error) {
        if (res.error.code === 'RATE_LIMITED') this.signalReadyAt = Date.now() + (res.error.retryAfterMs ?? SIGNAL_INTERVAL_MS);
        else toast(`Señal: ${label(ERROR_CODES, res.error.code)}`, 'bad');
      }
    } catch {
      toast('Sin conexión: la señal no salió.', 'bad');
    }
    this.render();
  }

  renderSignals() {
    const off = this.status !== 'IN_PROGRESS' || !this.transport.connected;
    const cooling = Date.now() < this.signalReadyAt;
    document.querySelectorAll('.signal').forEach((b) => {
      b.disabled = off || cooling;
      b.classList.toggle('cooling', cooling);
    });
    const p = this.signalMark;
    setText('signal-hint', p ? `Casilla (${p.x},${p.y}) marcada · Esc la quita` : 'Clic en el tablero: marca casilla');
    $('signal-hint').classList.toggle('marked', Boolean(p));
    const key = this.signalFeed.map((s) => s.shownAt).join();
    if (this.shown.signals !== key) {
      this.shown.signals = key;
      $('signal-feed').replaceChildren(...this.signalFeed.map((s) => {
        const phrase = SIGNALS[s.kind] ?? { text: s.kind, icon: '•' };
        return el('li', { class: s.fromPlayerId === this.playerId ? 'mine' : '' },
          el('span', { 'aria-hidden': 'true' }, phrase.icon), ' ', el('b', {}, this.nameOf(s.fromPlayerId)), `: ${phrase.text}`,
          s.position ? el('small', {}, ` (${s.position.x},${s.position.y})`) : null);
      }));
    }
  }

  /** Órdenes que oye el Campanero: lista privada (el tablero las dibuja sobre cada aliado). */
  renderLastWord() {
    const box = $('last-word');
    const heard = this.lastWord;
    box.hidden = !heard;
    if (!heard) {
      this.shown.lastWord = null;
      return;
    }
    if (this.shown.lastWord === heard) return;
    this.shown.lastWord = heard;
    setText('last-word-title', heard.typesOnly ? '🔔 Último en hablar: solo oyes los tipos' : '🔔 Último en hablar: esto harán');
    $('last-word-list').replaceChildren(...heard.orders.map((o) => el('li', {},
      el('b', {}, this.nameOf(o.playerId)), `: ${o.direction ? `${label(ORDER_TYPES, o.type)} ${label(DIRECTIONS, o.direction)}` : label(ORDER_TYPES, o.type)}`)));
  }

  /** Repique: cuenta regresiva pública en la barra de arriba. */
  renderBell() {
    const chip = $('m-bell');
    const bell = this.bell;
    chip.hidden = !bell;
    if (!bell) return;
    const who = bell.ringerId === this.playerId ? 'Tu Repique' : `Repique de ${this.nameOf(bell.ringerId)}`;
    if (bell.closed || bell.deadlineLocal === null || bell.deadlineLocal <= Date.now()) {
      chip.textContent = `🔔 ${who}: plazo vencido`;
      chip.className = 'badge bell over';
      return;
    }
    chip.textContent = `🔔 ${who}: ${Math.ceil((bell.deadlineLocal - Date.now()) / 1000)} s`;
    chip.className = 'badge bell ringing';
  }

  /** Copia de lo que se ve del tablero, para que las animaciones interpolen entre el antes y el después. */
  entities() {
    const copy = (v) => (v ? JSON.parse(JSON.stringify(v)) : null);
    return { me: copy(this.me), allies: copy(this.allies) ?? [], enemies: [...this.enemies.values()].map(copy) };
  }

  onSnapshot(m) {
    this.applySnapshot(m);
  }

  onDraftOffer(m) {
    this.draft = { ...m, deadlineLocal: Date.now() + Math.max(0, Date.parse(m.deadline) - Date.now()) };
    this.renderDraftOptions();
    setText('draft-source', m.source === 'ALTAR' ? '⛩ Altar' : '✦ Mejora y misión');
    this.syncDraftDialog();
    $('draft-options').querySelector('button')?.focus();
    emit('draft:offer', { options: m.options, source: m.source });
  }

  renderDraftOptions() {
    const m = this.draft;
    if (!m) return;
    $('draft-options').replaceChildren(...m.options.map((id) => el('button', {
      type: 'button',
      class: 'blessing',
      dataset: { blessing: id },
      onclick: () => this.pickBlessing(id),
    },
    el('span', { class: 'bl-ico', 'aria-hidden': 'true' }, BLESSING_ICONS[id] ?? '✧'),
    el('b', {}, label(BLESSINGS, id)),
    BLESSING_EFFECTS[id] ? el('small', {}, BLESSING_EFFECTS[id]) : null,
    BLESSING_COSTS[id] ? el('small', { class: 'bl-cost' }, `Precio: ${BLESSING_COSTS[id]}`) : null,
    el('span', { class: 'bl-cta', 'aria-hidden': 'true' }, 'Elegir'))));
  }

  /** El diálogo sigue abierto mientras falte la mejora o la misión; cada parte desaparece al elegirla. */
  syncDraftDialog() {
    const blessing = Boolean(this.draft);
    const pact = Boolean(this.pactOffer);
    $('draft').hidden = !blessing && !pact;
    $('draft-title').hidden = !blessing;
    $('draft-options').hidden = !blessing;
    $('draft-options').previousElementSibling.hidden = !blessing;
    $('pact-section').hidden = !pact;
    if (pact) setText('draft-source', '✦ Mejora y misión');
  }

  onPactOffer(m) {
    this.pactOffer = { ...m, sending: false };
    this.renderPactOptions();
    this.syncDraftDialog();
  }

  /** Se repinta al llegar la vista: el objetivo de la misión se muestra con el nombre del jugador. */
  renderPactOptions() {
    const offer = this.pactOffer;
    if (!offer) return;
    // Ya elegida en otra conexión: la vista lo dice.
    if (this.me?.pact && this.status !== 'DRAFTING') {
      this.pactOffer = null;
      this.syncDraftDialog();
      return;
    }
    $('pact-options').replaceChildren(...offer.options.map((o) => el('button', {
      type: 'button', class: 'pact-pick', disabled: offer.sending, dataset: { pact: o.pactId }, onclick: () => this.pickPact(o.pactId),
    },
    el('span', { class: 'seal', 'aria-hidden': 'true' }, '✶'),
    el('span', { class: 'pact-pick-text' }, o.description),
    o.targetId ? el('small', {}, `Tu objetivo: ${this.nameOf(o.targetId)}. Solo tú lo sabes.`) : null)));
  }

  async pickPact(pactId) {
    const offer = this.pactOffer;
    if (!offer || offer.sending) return;
    offer.sending = true;
    this.renderPactOptions();
    try {
      const res = await this.invoke('ChoosePact', { matchId: this.matchId, offerId: offer.offerId, pactId });
      if (res.error && res.error.code !== 'PACT_CLOSED') {
        toast(`Misión: ${label(ERROR_CODES, res.error.code)}`, 'bad');
        offer.sending = false;
        this.renderPactOptions();
        return;
      }
      if (this.pactOffer === offer) this.pactOffer = null;
      this.syncDraftDialog();
      if (!res.error) emit('draft:picked', { pactId });
      this.requestSnapshot();
    } catch {
      offer.sending = false;
      this.renderPactOptions();
      toast('Sin conexión; vuelve a elegir cuando reconecte.', 'bad');
    }
  }

  async pickBlessing(blessingId) {
    if (!this.draft) return;
    const { offerId } = this.draft;
    $('draft-options').querySelectorAll('button').forEach((b) => {
      b.disabled = true;
      b.classList.toggle('chosen', b.dataset.blessing === blessingId);
    });
    try {
      const res = await this.invoke('DraftPick', { matchId: this.matchId, offerId, blessingId });
      if (res.error && res.error.code !== 'DRAFT_CLOSED') {
        toast(`DraftPick: ${label(ERROR_CODES, res.error.code)}`, 'bad');
        $('draft-options').querySelectorAll('button').forEach((b) => { b.disabled = false; });
        return;
      }
      if (!res.error) {
        toast(`Elegiste ${label(BLESSINGS, res.blessingId)}`, 'ok');
        emit('draft:picked', { blessingId: res.blessingId });
      }
      this.draft = null;
      this.syncDraftDialog();
    } catch {
      $('draft-options').querySelectorAll('button').forEach((b) => { b.disabled = false; });
      toast('Sin conexión; vuelve a elegir cuando reconecte.', 'bad');
    }
  }

  // ───────────── caminos tras la extinción ─────────────

  onPathOffer(m) {
    if (this.me?.path?.choice && this.me.path.offerId === m.offerId) return; // ya elegido: el reenvío no reabre
    this.openPathDialog({
      offerId: m.offerId,
      savedEmbers: m.savedEmbers,
      vengeanceEmbers: m.vengeanceEmbers,
      friendshipEmbersMin: m.friendshipEmbersMin,
      friendshipEmbersMax: m.friendshipEmbersMax,
      wardenRoom: Boolean(m.wardenRoom),
      deadlineLocal: Date.parse(m.deadline) - this.clockOffset,
      precise: true,
    });
  }

  openPathDialog(offer) {
    const same = this.pathOffer?.offerId === offer.offerId;
    // Un PathOffer con el plazo exacto reemplaza los números aproximados de la vista, sin reiniciar la elección.
    this.pathOffer = { ...(same ? this.pathOffer : {}), ...offer, sending: same ? this.pathOffer.sending : false };
    const range = offer.friendshipEmbersMin === offer.friendshipEmbersMax
      ? String(offer.friendshipEmbersMin) : `${offer.friendshipEmbersMin}–${offer.friendshipEmbersMax}`;
    setText('path-saved', offer.savedEmbers);
    setText('path-friend-embers', range);
    setText('path-friend-label', offer.wardenRoom ? 'No renaces: al final puntúas con' : 'Renaces al bajar de piso con');
    setText('path-veng-embers', offer.vengeanceEmbers);
    setText('path-veng-label', offer.wardenRoom ? 'Apareces en 2 rondas junto al Celador con' : 'Apareces en la sala siguiente con');
    $('path-warden').hidden = !offer.wardenRoom;
    if (!same) {
      document.querySelectorAll('.path-card').forEach((b) => { b.disabled = false; b.classList.remove('chosen'); });
    }
    if ($('path').hidden) {
      $('path').hidden = false;
      this.armedMode = null;
      document.querySelector('.path-card[data-path="FRIENDSHIP"]')?.focus();
      emit('path:offer', { savedEmbers: offer.savedEmbers, wardenRoom: offer.wardenRoom });
    }
    this.renderTimer();
  }

  closePathDialog() {
    this.pathOffer = null;
    $('path').hidden = true;
  }

  async choosePath(path) {
    const offer = this.pathOffer;
    if (!offer || offer.sending) return;
    offer.sending = true;
    const cards = document.querySelectorAll('.path-card');
    cards.forEach((b) => { b.disabled = true; b.classList.toggle('chosen', b.dataset.path === path); });
    const unlock = () => {
      if (this.pathOffer !== offer) return;
      offer.sending = false;
      cards.forEach((b) => { b.disabled = false; b.classList.remove('chosen'); });
    };
    try {
      const res = await this.invoke('ChoosePath', { matchId: this.matchId, offerId: offer.offerId, path });
      if (res.error) {
        if (res.error.code === 'PATH_CLOSED') {
          // Ya se eligió otro camino, venció el plazo o la oferta era de otra extinción: la vista dirá cuál quedó.
          toast(`${label(ERROR_CODES, 'PATH_CLOSED')}: se usará lo que registró el servidor.`, 'info', 6000);
          if (this.pathOffer === offer) this.closePathDialog();
          this.requestSnapshot();
          return;
        }
        toast(`ChoosePath: ${label(ERROR_CODES, res.error.code)}`, 'bad');
        unlock();
        return;
      }
      // DUPLICATE_ACTION: la misma elección ya estaba registrada; vale igual que ACCEPTED.
      const chosen = res.path ?? path;
      if (this.me?.path) this.me.path.choice = chosen;
      toast(`Elegiste ${label(PATHS, chosen)}. Se revela al resolver la ronda.`, 'ok', 5000);
      emit('path:chosen', { path: chosen });
      if (this.pathOffer === offer) this.closePathDialog();
      this.render();
    } catch {
      unlock();
      toast('Sin conexión; vuelve a elegir cuando reconecte.', 'bad');
    }
  }

  onPlayerConnectionChanged(m) {
    const ally = this.allies.find((a) => a.playerId === m.playerId);
    if (ally) ally.connected = m.state === 'CONNECTED';
    const name = ally?.displayName ?? 'Un jugador';
    const text = { CONNECTED: 'volvió', DISCONNECTED: 'se desconectó', ABANDONED: 'abandonó la partida' }[m.state] ?? m.state;
    toast(`${name} ${text}`);
  }

  onMatchFinished(m) {
    this.finished = m;
    this.status = 'FINISHED';
    this.deadlineLocal = null;
    this.draft = null;
    this.pactOffer = null;
    $('draft').hidden = true;
    this.closePathDialog();
    showFinished(m, this.playerId, () => this.exit());
    this.bindRematch();
    emit('match:finished', { outcome: m.outcome, iWon: m.winners.includes(this.playerId), standings: m.standings, myId: this.playerId });
  }

  /** Revancha: solo con servidor real. Se avisa si otro ya la pidió; cada uno entra con un clic a la misma sala. */
  bindRematch() {
    const button = $('btn-rematch');
    const note = $('rematch-note');
    const real = this.onRematch && this.transport.protocolName !== 'demo' && this.transport.protocolName !== 'tutorial';
    const playable = real && !['FAULTED', 'TERMINATED'].includes(this.finished?.outcome);
    button.hidden = !playable;
    note.hidden = true;
    if (!playable) return;
    button.disabled = false;
    setText('btn-rematch', 'Revancha');
    button.onclick = async () => {
      button.disabled = true;
      clearTimeout(this.rematchTimer);
      try {
        const room = await api.rematch(this.matchId);
        const go = this.onRematch;
        this.dispose();
        if (active === this) active = null;
        go(room);
      } catch (err) {
        button.disabled = false;
        toast(`No se pudo pedir la revancha: ${err?.message ?? err}`, 'bad', 6000);
        this.pollRematch();
      }
    };
    this.pollRematch();
  }

  pollRematch() {
    clearTimeout(this.rematchTimer);
    const poll = async () => {
      if (this.disposed || $('screen-finished').hidden) return;
      try {
        const status = await api.rematchStatus(this.matchId);
        if (status?.available) {
          setText('rematch-note', `Ya hay revancha: ${status.players}/${status.capacity} en la sala. Pulsa Revancha para entrar con el mismo código.`);
          $('rematch-note').hidden = false;
          if (!$('btn-rematch').classList.contains('pulse')) toast('Alguien pidió la revancha.', 'info', 5000);
          setText('btn-rematch', 'Unirme a la revancha');
          $('btn-rematch').classList.add('pulse');
        }
      } catch {
        // Solo es un aviso: si falla, se reintenta.
      }
      this.rematchTimer = setTimeout(poll, REMATCH_POLL_MS);
    };
    this.rematchTimer = setTimeout(poll, 800);
  }

  onServerDraining(m) {
    this.drainDelayMs = m.reconnectAfterMs;
    toast('El servidor se está reiniciando; reconectando…');
  }

  onError(m) {
    toast(`Error del servidor: ${label(ERROR_CODES, m.error?.code)} — ${m.error?.message ?? ''}`, 'bad', 8000);
    if (m.error?.details?.status) this.status = m.error.details.status;
  }

  // ───────────── órdenes ─────────────

  handleKey(e) {
    if (this.disposed || $('screen-match').hidden || !$('draft').hidden || !$('path').hidden || !$('howto').hidden || settingsOpen()) return;
    const tag = document.activeElement?.tagName;
    if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return;
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    if (tag === 'BUTTON' && (e.key === ' ' || KEY_DIRS[e.key])) document.activeElement.blur();
    const dir = KEY_DIRS[e.key];
    if (dir) {
      e.preventDefault();
      this.planDirection(dir);
      return;
    }
    if (e.key === 'Enter') {
      if (tag === 'BUTTON') return;
      e.preventDefault();
      this.ready();
      return;
    }
    if (KEY_SIGNALS[e.key]) {
      this.sendSignal(KEY_SIGNALS[e.key]);
      return;
    }
    const key = e.key.toLowerCase();
    if (KEY_ACTIONS[key]) this.pickAction(KEY_ACTIONS[key]);
    else if (key === ' ') { e.preventDefault(); this.pickAction('GUARD'); }
    else if (key === 'escape') this.back();
  }

  // ───────────── plan por fases ─────────────

  /** ¿Se puede planear ahora? (ronda abierta, sin orden enviada y el héroe puede actuar). */
  planOpen() {
    if (this.status !== 'IN_PROGRESS' || this.deadlineLocal === null || !this.me || !canAct(this.me)) return false;
    if (this.me.decided || (this.order && this.order.payload.round === this.round && this.order.status !== 'error')) return false;
    return !this.orderBlocked({ type: 'MOVE' });
  }

  resetPlan(render) {
    this.plannedStep = null;
    this.plannedAction = null;
    this.armedMode = null;
    this.phase = 'MOVE';
    if (render) this.render();
  }

  goPhase(phase) {
    if (!this.planOpen()) return;
    if (phase === 'READY') { this.ready(); return; }
    this.phase = phase === 'ACT' ? 'ACT' : 'MOVE';
    this.armedMode = null;
    this.render();
  }

  back() {
    if (this.armedMode) this.armedMode = null;
    else if (this.phase === 'ACT') this.phase = 'MOVE';
    else this.signalMark = null;
    this.render();
  }

  /** Fase 2: elegir una acción. Defender no tiene objetivo y borra el paso. */
  pickAction(type) {
    if (!this.planOpen()) { if (this.me && canAct(this.me)) toast('Ya enviaste tu orden de esta ronda.'); return; }
    const blocked = this.orderBlocked({ type });
    if (blocked) { toast(blocked); return; }
    this.phase = 'ACT';
    if (type === 'GUARD') {
      this.plannedStep = null;
      this.plannedAction = { type: 'GUARD' };
      this.armedMode = null;
    } else {
      this.armedMode = this.armedMode === type ? null : type;
      emit('order:armed', { type });
    }
    this.render();
  }

  /** Flecha (atajo): en la fase 1 es el paso; en la 2, el objetivo de la acción elegida. */
  planDirection(direction) {
    if (!this.planOpen()) return;
    if (this.phase === 'ACT' && this.armedMode) this.setAction(this.armedMode, direction);
    else if (this.phase === 'MOVE') this.setStep(direction);
  }

  setStep(direction) {
    this.plannedStep = direction;
    if (this.plannedAction?.type === 'GUARD' && direction) this.plannedAction = null;
    this.phase = 'ACT';
    emit('order:step', { direction });
    this.render();
  }

  setAction(type, direction) {
    this.plannedAction = { type, direction };
    this.armedMode = null;
    emit('order:planned', { type, direction });
    this.render();
  }

  /** Clic en una casilla: si es un objetivo resaltado de la fase actual, entra en el plan. */
  planTile(p) {
    if (!this.planOpen()) return false;
    if (this.phase === 'MOVE') {
      const origin = this.board.origin();
      if (origin && origin.x === p.x && origin.y === p.y) { this.setStep(null); return true; }
      const dir = this.board.dirForTile('MOVE', p.x, p.y, null);
      if (!dir) return false;
      this.setStep(dir);
      return true;
    }
    if (!this.armedMode) return false;
    const dir = this.board.dirForTile(this.armedMode, p.x, p.y, this.plannedStep);
    if (!dir) return false;
    this.setAction(this.armedMode, dir);
    return true;
  }

  /** La orden que sale del plan, o null si no hay nada planeado. */
  planOrder() {
    const action = this.plannedAction;
    if (action?.type === 'GUARD') return { type: 'GUARD' };
    if (action) return this.plannedStep ? { ...action, step: this.plannedStep } : { ...action };
    if (this.plannedStep) return { type: 'MOVE', direction: this.plannedStep };
    return null;
  }

  /** Listo: envía lo planeado. Sin nada planeado, defiende. */
  ready() {
    if (!this.planOpen()) return;
    this.submitOrder(this.planOrder() ?? { type: 'GUARD' });
  }

  /** Resumen del plan en lenguaje simple. */
  planSummary() {
    const me = this.me;
    if (!me) return '';
    const ember = me.state === 'EMBER_SPIRIT';
    const walk = this.plannedStep ? `${ember ? 'Flotas' : 'Te mueves'} al ${label(DIRECTIONS, this.plannedStep)}` : 'Te quedas donde estás';
    const a = this.plannedAction;
    if (a?.type === 'GUARD') return ember ? 'Te quedas y enciendes una Luz.' : 'Te quedas y defiendes: recibes la mitad del daño.';
    if (!a) return `${walk}${this.plannedStep ? '' : ''}. Falta la acción (o pulsa Listo y solo caminas).`;
    const target = this.board.describeTarget(a.type, a.direction, this.plannedStep);
    const names = ember ? EMBER_ORDERS : heroOrders(me);
    const verb = {
      ATTACK: ember ? 'frenas' : 'atacas', INTERACT: ember ? 'revives' : 'usas', PUSH: 'empujas',
      ABILITY: `usas ${(names.ABILITY?.name ?? 'tu habilidad')}`,
    }[a.type] ?? label(ORDER_TYPES, a.type).toLowerCase();
    const to = target ? ` ${a.type === 'ABILITY' ? 'hacia' : 'a'} ${target}` : ` hacia el ${label(DIRECTIONS, a.direction)}`;
    const noAim = a.type === 'ABILITY' && !ember && me.hostEnemyId == null && NO_AIM.has(me.hero);
    return `${walk} y ${verb}${noAim ? '' : to}.`;
  }

  /** Motivo por el que la orden no puede salir ahora (o null). Evita gastar la orden de la ronda en algo que no vale. */
  orderBlocked(order) {
    const me = this.me;
    if (!me) return null;
    if (me.state === 'EXTINCT') return me.path?.choice ? 'Ya elegiste camino: se revela al resolver la ronda.' : 'Moriste: elige tu camino.';
    if (awaitingSpawn(me)) return 'Todavía no apareciste como Traidor.';
    if (this.bell && this.bell.ringerId !== this.playerId && (this.bell.closed || this.deadlineLocal <= Date.now())) return 'Se acabó tu plazo del Repique: defiendes esta ronda.';
    if (order.type === 'PUSH') {
      if (me.state === 'EMBER_SPIRIT') return 'Un Fantasma no puede empujar.';
      if (me.hostEnemyId != null) return 'Dentro de un huésped no puedes empujar.';
      if ((me.pushCooldown ?? 0) > 0) return `Podrás empujar en ${me.pushCooldown} ronda${me.pushCooldown === 1 ? '' : 's'}.`;
      return null;
    }
    if (me.state !== 'EMBER_SPIRIT') {
      const cd = me.abilityCooldown ?? 0;
      const name = me.hostEnemyId != null ? 'Salir' : heroInfo(me.hero).ability || 'La habilidad';
      if (order.type === 'ABILITY' && cd > 0 && me.hostEnemyId == null) return `${name} se enfría: lista en ${cd} ronda${cd === 1 ? '' : 's'}.`;
      return null;
    }
    if (order.type === 'ABILITY' && me.abilityCooldown > 0) return `Escudo en espera: listo en ${me.abilityCooldown} ronda${me.abilityCooldown === 1 ? '' : 's'}.`;
    const hinder = me.path?.hinderCooldown ?? 0;
    if (order.type === 'ATTACK' && hinder > 0) return `Frenar en espera: listo en ${hinder} ronda${hinder === 1 ? '' : 's'}.`;
    return null;
  }

  submitOrder(order) {
    if (this.status !== 'IN_PROGRESS' || this.deadlineLocal === null) {
      toast('No hay una ronda abierta.');
      this.render();
      return;
    }
    const blocked = this.orderBlocked(order);
    if (blocked) {
      toast(blocked);
      emit('order:rejected', { order, code: 'CLIENT_BLOCKED' });
      this.render();
      return;
    }
    if (this.order && this.order.payload.round === this.round && this.order.status !== 'error') {
      toast('Ya enviaste tu orden de esta ronda (la primera es la que vale).');
      this.render();
      return;
    }
    this.order = {
      payload: { matchId: this.matchId, round: this.round, clientActionId: uuidv7(), order },
      status: 'sending',
      ack: null,
      error: null,
      attempts: 0,
    };
    emit('order:sent', { order });
    this.render();
    this.sendOrder();
  }

  async sendOrder() {
    const o = this.order;
    if (!o) return;
    if (!this.transport.connected) {
      o.status = 'waiting';
      this.render();
      return;
    }
    o.status = 'sending';
    o.attempts += 1;
    this.render();
    try {
      const res = await this.invoke('SubmitOrder', o.payload);
      if (this.order !== o) return;
      if (res.error) {
        if (RETRYABLE.has(res.error.code) && o.attempts < 5) {
          // Mismo clientActionId: si el servidor ya la registró, responde DUPLICATE_ACTION.
          o.status = 'waiting';
          setTimeout(() => { if (this.order === o) this.sendOrder(); }, res.error.retryAfterMs ?? 500);
        } else {
          o.status = 'error';
          o.error = res.error;
          emit('order:rejected', { order: o.payload.order, code: res.error.code });
          if (res.error.code === 'ORDER_TOO_LATE') toast(label(ERROR_CODES, 'ORDER_TOO_LATE'), 'info', 5000);
          if (res.error.code === 'STALE_ROUND' && res.error.details?.currentRound) this.round = res.error.details.currentRound;
        }
      } else {
        o.status = 'acked';
        o.ack = res;
        emit('order:acked', { order: o.payload.order });
        if (this.me) { this.me.decided = true; this.me.pendingOrder = o.payload.order; }
      }
    } catch (err) {
      // La conexión cayó con la invocación en vuelo: no sabemos si llegó. Se reenvía al reconectar.
      if (this.order === o) o.status = 'waiting';
      logMessage('sys', 'SubmitOrderLost', { clientActionId: o.payload.clientActionId, message: String(err?.message ?? err) });
    }
    this.render();
  }

  async resendPendingOrder() {
    const o = this.order;
    if (!o || (o.status !== 'waiting' && o.status !== 'sending')) return;
    if (o.payload.round !== this.round) {
      this.order = null;
      return;
    }
    logMessage('sys', 'ResendOrder', { clientActionId: o.payload.clientActionId });
    await this.sendOrder();
  }

  async leave() {
    // eslint-disable-next-line no-alert
    if (!window.confirm('¿Abandonar la partida? En curso cuenta como abandono y pierdes tu oro.')) return;
    try {
      if (this.transport.connected) await this.invoke('LeaveMatch', { matchId: this.matchId });
    } catch {
      // Se sale igual.
    }
    this.exit();
  }

  // ───────────── render ─────────────

  render() {
    if (this.disposed) return;
    const me = this.me;
    setText('m-floor', this.floor ?? '–');
    setText('m-round', this.round || '–');
    setText('m-status', this.status ? label(MATCH_STATUS, this.status) : '');
    this.renderRoom();

    // Héroe propio.
    if (me) this.renderMe(me);

    // Jugadores y quién decidió.
    const players = me ? [{ ...me, connected: this.transport.connected, isMe: true }, ...this.allies] : this.allies;
    const decidedNow = new Set();
    $('players').replaceChildren(...players.map((p) => this.playerItem(p, decidedNow)));
    this.shown.decided = decidedNow;
    setText('m-decided', this.status === 'IN_PROGRESS' && this.expectedPlayers
      ? `${this.decidedCount}/${this.expectedPlayers} decidieron` : '');
    $('m-decided').className = this.expectedPlayers && this.decidedCount >= this.expectedPlayers ? 'badge ok' : 'badge';

    const mode = this.armedMode ?? (this.phase === 'MOVE' ? 'MOVE' : this.plannedAction?.type ?? 'MOVE');
    this.renderOrderModes(me);
    this.renderPlanner(me);
    this.renderOrderNote(me, mode);
    this.renderOrderStatus();
    this.renderLastWord();
    this.renderSignals();
    this.renderBell();
    $('legend-paths').hidden = ![me, ...this.allies].some((p) => p?.state === 'EMBER_SPIRIT' || p?.state === 'RENEGADE');
    $('legend-hidden').hidden = !this.hiddenTelegraphs.length;
    $('legend-echo').hidden = !this.echoes.length;
    $('legend-noise').hidden = !this.noiseTiles.length;
    $('legend-ground').hidden = !this.groundEmbers.length;

    // Eventos de la última ronda (se rehacen solo cuando llega una ronda nueva, para no repetir la animación).
    if (this.shown.events !== this.events) {
      this.shown.events = this.events;
      $('events').replaceChildren(...(this.events.length
        ? this.eventItems(this.events)
        : [el('li', { class: 'empty' }, 'Todavía no pasó nada en esta ronda.')]));
    }

    this.board.draw({
      tiles: this.tiles,
      me,
      allies: this.allies,
      enemies: this.enemies,
      pendingOrder: this.order?.status === 'acked' || this.order?.status === 'sending' || this.order?.status === 'waiting'
        ? this.order.payload.order : me?.pendingOrder ?? null,
      armedMode: this.planOpen() ? (this.phase === 'MOVE' ? 'MOVE' : this.armedMode) : null,
      mode,
      plannedStep: this.plannedStep,
      plannedAction: this.planOpen() ? this.plannedAction : null,
      size: this.room?.size ?? 9,
      floodedRows: this.room?.floodedRows ?? 0,
      round: this.round,
      hiddenTelegraphs: this.hiddenTelegraphs,
      echoes: this.echoes,
      groundEmbers: this.groundEmbers,
      noiseTiles: this.noiseTiles,
      bell: this.bell,
      lastWord: this.lastWord,
      signalMark: this.signalMark,
    });
    this.renderTimer();
  }

  /** Sala y maldiciones: públicas, siempre a la vista. */
  renderRoom() {
    const type = this.room?.type ?? 'NORMAL';
    const known = ROOM_TYPES[type] ?? {};
    const name = this.room?.name ?? known.name ?? type;
    const effect = this.room?.effect ?? known.effect ?? '';
    const flood = this.room?.floodedRows ?? 0;
    const key = `${type}|${flood}|${this.curses.join()}`;
    if (this.shown.room === key) return;
    this.shown.room = key;
    setText('m-room-name', `${known.icon ?? ''} ${name}`.trim());
    $('m-room').title = effect;
    const note = $('room-note');
    note.hidden = type === 'NORMAL';
    note.textContent = type === 'FLOODED' && flood > 0 ? `${effect} Agua: ${flood} fila${flood === 1 ? '' : 's'}.` : effect;
    $('legend-water').hidden = !flood;
    $('m-curses').replaceChildren(...this.curses.map((c) => el('span', { class: 'curse-chip', title: `${CURSES[c]?.name ?? c}: ${CURSES[c]?.effect ?? ''}`, 'aria-label': CURSES[c]?.name ?? c }, CURSES[c]?.icon ?? '☠')));
  }

  /** Indicador de fases, ayuda, ficha de la habilidad, resumen y Listo. */
  renderPlanner(me) {
    const open = this.planOpen();
    const sent = Boolean(this.me?.decided || (this.order && this.order.payload.round === this.round && this.order.status !== 'error'));
    const phase = sent ? 'READY' : this.phase;
    document.querySelectorAll('.phase-btn').forEach((b) => {
      const p = b.dataset.phase;
      b.classList.toggle('current', p === phase);
      b.classList.toggle('done', (p === 'MOVE' && (phase !== 'MOVE')) || (p === 'ACT' && phase === 'READY'));
      b.disabled = !open && !(p === 'READY' && sent);
      b.setAttribute('aria-current', p === phase ? 'step' : 'false');
    });
    setText('plan-round', this.round ? `Ronda ${this.round}` : '');
    const ember = me?.state === 'EMBER_SPIRIT';
    let help = '';
    if (sent) help = '✔ Plan enviado. Espera a los demás.';
    else if (!open) help = me && !canAct(me) ? 'Esta ronda no das órdenes.' : this.status === 'IN_PROGRESS' ? 'Espera la próxima ronda.' : '';
    else if (this.phase === 'MOVE') help = ember ? '1 · Flotar: clic en una casilla resaltada (hasta 2 en línea), o en ti para quedarte.' : '1 · Moverte: clic en una casilla vecina resaltada, o en tu héroe para quedarte.';
    else if (this.armedMode) help = `2 · ${(ember ? EMBER_PICK_TEXT : PICK_TEXT)[this.armedMode] ?? 'Clic en un objetivo resaltado.'}`;
    else help = '2 · Actuar: elige una acción abajo, o pulsa Listo.';
    setText('phase-help', help);
    $('phase-help').className = `phase-help${this.armedMode ? ' picking' : ''}`;
    document.querySelectorAll('.act').forEach((b) => {
      const on = b.dataset.act === this.armedMode || (!this.armedMode && b.dataset.act === this.plannedAction?.type);
      b.classList.toggle('armed', b.dataset.act === this.armedMode);
      b.classList.toggle('chosen', on && b.dataset.act !== this.armedMode);
      b.setAttribute('aria-pressed', String(on));
    });
    $('act-buttons').classList.toggle('locked', !open);
    // Ficha de la habilidad: qué hace y su espera.
    const info = heroInfo(me?.hero);
    const names = ember ? EMBER_ORDERS : heroOrders(me);
    const abilityName = names.ABILITY?.name ?? info.ability ?? 'Habilidad';
    const cd = ember ? me?.abilityCooldown ?? 0 : me?.hostEnemyId != null ? 0 : me?.abilityCooldown ?? 0;
    setText('ability-name', `✺ ${abilityName}`);
    setText('ability-wait', cd > 0 ? `espera ${cd}` : 'lista');
    $('ability-wait').className = cd > 0 ? 'badge warn' : 'badge ok';
    setText('ability-text', ember ? (EMBER_ORDERS.ABILITY?.text ?? '') : me?.hostEnemyId != null ? (names.ABILITY?.text ?? '') : info.abilityText ?? '');
    $('ability-card').hidden = !me || me.state === 'EXTINCT' || me.state === 'DOWN';
    setText('plan-summary', open ? this.planSummary() : sent && this.order ? `Enviado: ${describeOrder(this.order.payload.order)}` : '');
    $('btn-ready').disabled = !open;
    $('btn-plan-reset').disabled = !open || (!this.plannedStep && !this.plannedAction && this.phase === 'MOVE');
    $('btn-ready').textContent = open && !this.planOrder() ? 'Listo (defender)' : 'Listo';
  }

  /** Fila de la lista de jugadores: estado del camino, si decidió y su vida (la Brasa no tiene vida). */
  playerItem(p, decidedNow) {
    const info = heroInfo(p.hero);
    const kind = pathClass(p.state);
    const absent = p.state === 'RENEGADE' && !p.position && !p.isMe;
    const active = p.isMe ? canAct(p) : p.state === 'ALIVE' || p.state === 'EMBER_SPIRIT' || (p.state === 'RENEGADE' && (p.position || p.decided));
    const pct = p.maxHp ? Math.max(0, Math.min(100, (p.hp / p.maxHp) * 100)) : 0;
    if (p.decided) decidedNow.add(p.playerId);
    let state;
    if (p.state === 'EXTINCT') {
      state = el('span', { class: 'badge warn p-state thinking' }, p.isMe && p.path?.choice ? 'eligió' : 'eligiendo',
        el('span', { class: 'dots', 'aria-hidden': 'true' }, el('i', {}, '.'), el('i', {}, '.'), el('i', {}, '.')));
    } else if (!active) {
      const text = p.state === 'RENEGADE' ? (absent ? 'acecha' : 'por aparecer') : label(HERO_STATES, p.state);
      state = el('span', { class: `badge ${kind || 'bad'} p-state` }, text);
    } else if (p.decided) {
      const fresh = !this.shown.decided.has(p.playerId);
      state = el('span', { class: `badge ok p-state${fresh ? ' decided' : ''}` }, '✔ decidió');
    } else {
      state = el('span', { class: 'badge p-state thinking' }, 'pensando',
        el('span', { class: 'dots', 'aria-hidden': 'true' }, el('i', {}, '.'), el('i', {}, '.'), el('i', {}, '.')));
    }
    const marks = { ember: ['❂', 'Fantasma'], renegade: ['☠', 'Traidor: del lado de la mazmorra'], choosing: ['🕯', 'Muerto: eligiendo camino'] };
    const mark = kind ? el('span', { class: `p-mark ${kind}`, title: marks[kind][1], 'aria-hidden': 'true' }, marks[kind][0]) : null;
    let stats;
    if (p.state === 'EMBER_SPIRIT') {
      stats = [el('span', { class: 'p-tag ember' }, '❂ Fantasma')];
    } else if (p.hero === 'AVARO') {
      // Sus ascuas son su vida: no hay barra (el número de ascuas va al final de la fila).
      stats = [p.state === 'RENEGADE' ? el('span', { class: 'p-tag renegade' }, '☠ Traidor') : el('span', { class: 'p-tag' }, 'vive de su oro')];
    } else {
      const hp = el('span', { class: pct <= 34 ? 'mini-hp low' : 'mini-hp', 'aria-hidden': 'true' }, el('i'));
      hp.firstChild.style.width = `${pct}%`;
      stats = [
        p.state === 'RENEGADE' ? el('span', { class: 'p-tag renegade' }, '☠ Traidor') : p.state === 'EXTINCT' ? el('span', { class: 'p-tag choosing' }, '🕯 Muerto') : hp,
        el('span', {}, `♥ ${p.hp}/${p.maxHp}`),
      ];
    }
    const classes = [p.isMe ? 'me' : '', kind, p.state === 'DOWN' || p.state === 'RETIRED' ? 'down' : ''].filter(Boolean).join(' ');
    return el('li', { class: classes, dataset: { hero: p.hero } },
      el('span', { class: 'p-avatar' }, heroAvatar(p.hero, 'sm'), mark,
        el('span', { class: `dot ${p.connected ? 'on' : 'off'}`, title: p.connected ? 'conectado' : 'desconectado' })),
      el('span', { class: 'who' }, p.displayName, p.isMe ? ' (tú)' : '', ' ', el('small', {}, `· ${info.name}`)),
      state,
      el('span', { class: 'p-stats' }, ...stats, el('span', { class: 'em' }, `✦ ${p.embers}`),
        // Lo que te debe solo llega si eres su Usurera; un aliado sin posición puede ir de polizón o no oírse.
        p.debtToYou ? el('span', { class: 'p-tag debt', title: 'Solo tú lo ves' }, `⚖ te debe ${p.debtToYou}`) : null,
        !p.isMe && !p.position && p.hero === 'POLIZON' && p.state === 'ALIVE' ? el('span', { class: 'p-tag ghost' }, 'sin rastro') : null,
        !p.isMe && !p.position && p.hero !== 'POLIZON' && p.state === 'ALIVE' && isBlindMe(this.me) ? el('span', { class: 'p-tag ghost' }, 'no lo oyes') : null));
  }

  /** Botones de orden: los de la Brasa cambian de nombre e ícono y muestran su enfriamiento. */
  renderOrderModes(me) {
    const ember = me?.state === 'EMBER_SPIRIT';
    const hosted = me?.hostEnemyId != null;
    const cooldowns = ember ? { ABILITY: me.abilityCooldown ?? 0, ATTACK: me.path?.hinderCooldown ?? 0 } : { ABILITY: hosted ? 0 : me?.abilityCooldown ?? 0 };
    cooldowns.PUSH = ember ? 0 : me?.pushCooldown ?? 0;
    const canPush = !ember && !hosted && (me?.state === 'ALIVE' || me?.state === 'RENEGADE');
    const key = `${ember}|${hosted}|${me?.hero}|${cooldowns.ABILITY ?? ''}|${cooldowns.ATTACK ?? ''}|${cooldowns.PUSH}|${canPush}`;
    const card = document.querySelector('.card-orders');
    card.dataset.path = pathClass(me?.state) || 'hero';
    card.classList.toggle('inactive', Boolean(me) && !canAct(me));
    if (this.shown.orders === key) return;
    this.shown.orders = key;
    const names = ember ? EMBER_ORDERS : heroOrders(me);
    document.querySelectorAll('.act').forEach((b) => {
      const type = b.dataset.act;
      const named = names[type] ?? HERO_ORDERS[type];
      b.querySelector('.act-name').textContent = named.name;
      b.querySelector('.act-ico').textContent = named.icon;
      b.title = named.text ? `${named.name}: ${named.text}` : '';
      if (type === 'PUSH') { b.disabled = !canPush; b.hidden = ember; b.title = canPush ? 'Empujar: mueve 1 casilla a quien tengas al lado. Con autor. Espera 3.' : 'Ahora no puedes empujar.'; }
      const cd = cooldowns[type] ?? 0;
      const badge = b.querySelector('.act-cd');
      badge.hidden = cd <= 0;
      badge.textContent = cd > 0 ? String(cd) : '';
      badge.title = cd > 0 ? `Lista en ${cd} ronda${cd === 1 ? '' : 's'}` : '';
      b.classList.toggle('cooling', cd > 0);
    });
  }

  /** Nota bajo los modos: qué hace la orden de la Brasa, o por qué no se puede dar orden. */
  renderOrderNote(me, mode) {
    const note = $('order-note');
    let kind = '';
    let parts = null;
    if (me?.state === 'EMBER_SPIRIT') {
      const o = EMBER_ORDERS[mode] ?? EMBER_ORDERS.MOVE;
      kind = 'ember';
      parts = [el('b', {}, `${o.name}: `), o.text, mode === 'GUARD' ? '' : ' Sin orden, enciendes una Luz.'];
    } else if (me?.state === 'EXTINCT') {
      kind = 'wait';
      parts = [me.path?.choice ? `Elegiste ${label(PATHS, me.path.choice)}; se revela al resolver la ronda.` : 'Moriste: elige tu camino.'];
    } else if (awaitingSpawn(me)) {
      kind = 'renegade';
      parts = [this.spawnText(me)];
    } else if (me?.state === 'RENEGADE') {
      kind = 'renegade';
      parts = [el('b', {}, 'Traidor: '), 'tus órdenes son las de tu héroe, pero los monstruos no te atacan y solo dañas a los héroes (máx. 1 por golpe).'];
    } else if (me?.state === 'DOWN') {
      kind = 'wait';
      parts = ['Estás derribado: un aliado (o un Fantasma) puede levantarte con Usar.'];
    } else if (me?.hostEnemyId != null) {
      kind = 'host';
      const o = heroOrders(me)[mode] ?? heroOrders(me).MOVE;
      parts = [el('b', {}, `Dentro de ${this.hostName(me)}: `), o.text ?? 'Nadie te ve.'];
    } else if (me && mode === 'PUSH') {
      parts = [el('b', {}, 'Empujón: '), 'pinchos y fuego hacen su daño; un monstruo que no vuela muere en una grieta y un héroe queda derribado.'];
    } else if (me?.hero === 'HILVAN' && (mode === 'MOVE' || mode === 'ATTACK' || mode === 'INTERACT')) {
      parts = [el('b', {}, 'Espejo: '), 'norte y sur van igual; este y oeste, al revés. Cada cuerpo rebota por su cuenta.'];
    }
    note.hidden = !parts;
    note.className = kind ? `order-note ${kind}` : 'order-note';
    const text = parts ? parts.map((p) => (typeof p === 'string' ? p : p.textContent)).join('') : '';
    if (note.dataset.text !== text) {
      note.dataset.text = text;
      note.replaceChildren(...(parts ?? []));
    }
  }

  /** Nombre del huésped de Polizón. */
  hostName(me) {
    const host = this.enemies.get(me.hostEnemyId);
    return host ? label(ENEMIES, host.kind) : 'tu huésped';
  }

  /** Lo que cada héroe necesita tener a la vista; solo con datos de su propia vista. */
  heroNote(me) {
    if (me.state === 'EMBER_SPIRIT' || me.state === 'EXTINCT') return null;
    switch (me.hero) {
      case 'ANTEAYER':
        if (me.rewindTile) return `Volver: a (${me.rewindTile.x},${me.rewindTile.y}) con ${me.rewindHp ?? '?'} de vida. Ecos pendientes: ${this.echoes.filter((e) => e.ownerId === me.playerId).length}.`;
        return `Volver: sin 2 rondas de historia en esta sala. Ecos pendientes: ${this.echoes.filter((e) => e.ownerId === me.playerId).length}.`;
      case 'HILVAN':
        return me.secondBody ? `Segundo cuerpo en (${me.secondBody.x},${me.secondBody.y}). Una sola vida: un golpe a cada cuerpo son dos golpes.` : null;
      case 'POLIZON': {
        if (me.hostEnemyId == null) return 'A la vista. Aborda a un enemigo vecino para desaparecer.';
        const host = this.enemies.get(me.hostEnemyId);
        return host ? `A bordo de ${label(ENEMIES, host.kind)} (${host.hp}/${host.maxHp}). Nadie te ve.` : 'A bordo. Nadie te ve.';
      }
      case 'ESCUCHA':
        return `A oscuras más allá de tu alcance. Oíste ${this.noiseTiles.length} ruido${this.noiseTiles.length === 1 ? '' : 's'} en la última resolución.`;
      case 'AVARO':
        return me.embers <= 1 ? 'Te queda 1 de oro o menos: otro golpe y caes.' : 'Cada punto de daño tira 1 de oro al suelo. Pisa la casilla para recuperarlo.';
      case 'CAMPANERO':
        return this.lastWord ? 'Ya oíste a los demás: responde con lo que quede de reloj.' : 'Si esperas a que todos decidan, oirás sus órdenes.';
      default:
        return null;
    }
  }

  /** Cuándo aparece el Renegado que espera. */
  spawnText(me) {
    const p = me.path ?? {};
    if (p.awaitingSpawn === 'ROUND' && p.spawnRound) {
      const left = Math.max(0, p.spawnRound - this.round);
      return left > 0
        ? `Aparecerás junto al Celador al terminar la ronda ${p.spawnRound} (faltan ${left} ronda${left === 1 ? '' : 's'}).`
        : 'Aparecerás junto al Celador al terminar esta ronda.';
    }
    return 'Aparecerás cuando el grupo baje a la sala siguiente, en la casilla más lejana de los héroes.';
  }

  renderMe(me) {
    const info = heroInfo(me.hero);
    const card = document.querySelector('.card-hero');
    if (this.shown.hero !== me.hero) {
      this.shown.hero = me.hero;
      card.dataset.hero = me.hero;
      $('me-portrait').replaceChildren(heroAvatar(me.hero, 'lg'));
    }
    setText('me-title', me.displayName);
    const kind = pathClass(me.state);
    const roles = { ember: 'espíritu', renegade: 'enemigo', choosing: 'sin cuerpo' };
    setText('me-role', `${info.name} · ${roles[kind] ?? info.role}`);
    if (kind) card.dataset.path = kind; else delete card.dataset.path;
    const ember = kind === 'ember';
    const miser = me.hero === 'AVARO';
    $('me-hearts').hidden = ember;
    $('me-hpwrap').hidden = ember || miser;
    $('me-tiles').hidden = ember;
    this.renderPathPanel(me, kind);
    const note = this.heroNote(me);
    $('me-note').hidden = !note;
    setText('me-note', note ?? '');

    // Vida: corazones + barra; si bajó, sacudida y corazones perdidos.
    const prev = this.shown.hp;
    const lost = prev !== null && me.hp < prev;
    const gained = prev !== null && me.hp > prev;
    this.shown.hp = me.hp;
    const hearts = [];
    if (miser) {
      // Sin barra de vida: se dibujan sus ascuas, una a una.
      for (let i = 0; i < Math.min(me.embers, 12); i++) hearts.push(el('span', { class: 'heart ember-life' }));
      if (me.embers > 12) hearts.push(el('span', { class: 'ember-more' }, `+${me.embers - 12}`));
    } else {
      for (let i = 0; i < me.maxHp; i++) {
        const full = i < me.hp;
        let cls = full ? 'heart' : 'heart empty';
        if (lost && i >= me.hp && i < prev) cls += ' lost';
        if (gained && i < me.hp && i >= prev) cls += ' gained';
        hearts.push(el('span', { class: cls }));
      }
    }
    for (let i = 0; i < (me.shield ?? 0); i++) hearts.push(el('span', { class: 'heart shield' }));
    $('me-hearts').replaceChildren(...hearts);
    if (lost || (miser && this.shown.embers !== null && me.embers < this.shown.embers)) replay(card, 'hit');
    const pct = Math.max(0, Math.min(100, (me.hp / me.maxHp) * 100));
    const hpbar = $('me-hpbar');
    hpbar.style.width = `${pct}%`;
    hpbar.className = pct <= 34 ? 'bar-hp low' : pct <= 60 ? 'bar-hp mid' : 'bar-hp';
    setText('me-hp', `Vida ${me.hp}/${me.maxHp}${me.shield ? ` + escudo ${me.shield}` : ''}`);
    setText('me-shield', me.shield);

    // Ascuas: el número salta y flota el +N / −N.
    const tile = $('tile-embers');
    if (this.shown.embers !== null && me.embers !== this.shown.embers) {
      const delta = me.embers - this.shown.embers;
      replay(tile, delta > 0 ? 'bump' : 'drop');
      const float = el('span', { class: delta > 0 ? 'float-delta' : 'float-delta neg', 'aria-hidden': 'true' }, `${delta > 0 ? '+' : '−'}${Math.abs(delta)}`);
      tile.append(float);
      setTimeout(() => float.remove(), 1300);
    }
    this.shown.embers = me.embers;
    setText('me-embers', me.embers);

    // Deuda propia (y con quién) o, para la Usurera, lo que le deben. Nadie más lo ve.
    const owed = this.allies.reduce((sum, a) => sum + (a.debtToYou ?? 0), 0);
    const debtTile = $('tile-debt');
    debtTile.hidden = !me.debt && !owed;
    if (me.debt) {
      setText('me-debt', me.debt);
      setText('me-debt-label', `Debes a ${this.nameOf(me.debtTo)}`);
      debtTile.title = 'Todo el oro que ganes le llega hasta saldar la deuda. Cada 7 rondas sube 1, hasta 3.';
    } else if (owed) {
      setText('me-debt', owed);
      setText('me-debt-label', 'Te deben');
      debtTile.title = 'Solo tú lo ves. Cobras todo el oro que ganen y, al final, lo que quede.';
    }

    // Enfriamiento de la habilidad.
    const ready = !me.abilityCooldown;
    setText('me-cooldown', ready ? 'Lista' : me.abilityCooldown);
    setText('me-ability', info.ability || 'Habilidad');
    $('tile-ability').className = `tile tile-ability ${ready ? 'ready' : 'cooling'}`;
    $('tile-ability').title = ready ? `${info.ability}: lista para usar` : `${info.ability}: faltan ${me.abilityCooldown} rondas`;

    const downNote = me.state === 'DOWN' && me.downRoundsLeft !== null ? ` (${me.downRoundsLeft} rondas)` : '';
    setText('me-state', `${label(HERO_STATES, me.state)}${downNote}`);
    const badge = { ALIVE: 'ok', DOWN: 'bad', EXTINCT: 'warn', EMBER_SPIRIT: 'ember', RENEGADE: 'renegade' }[me.state];
    $('me-state').className = badge ? `badge ${badge}` : 'badge';

    // Bendiciones (chips con su efecto al pasar el cursor o con el foco) y pacto secreto.
    const blessingKey = me.blessings.join(',');
    if (this.shown.blessings !== blessingKey) {
      this.shown.blessings = blessingKey;
      $('me-blessings').replaceChildren(...(me.blessings.length
        ? me.blessings.map((b) => el('li', { tabindex: 0, 'data-tip': BLESSING_EFFECTS[b] ?? '', 'aria-label': `${label(BLESSINGS, b)}: ${BLESSING_EFFECTS[b] ?? ''}` },
          el('span', { 'aria-hidden': 'true' }, BLESSING_ICONS[b] ?? '✧'), label(BLESSINGS, b)))
        : [el('li', { class: 'none' }, 'ninguna todavía')]));
    }
    const pact = me.pact ? `${me.pact.description}${me.pact.targetId ? ` Tu objetivo: ${this.nameOf(me.pact.targetId)}.` : ''}` : 'Sin elegir todavía';
    if (this.shown.pact !== pact) {
      this.shown.pact = pact;
      $('me-pact').replaceChildren(el('p', {}, pact));
    }
  }

  /** Panel del camino en la tarjeta del héroe: eligiendo, Brasa (ecos y renacer) o Renegado. */
  renderPathPanel(me, kind) {
    const panel = $('me-path');
    panel.hidden = !kind;
    if (!kind) {
      this.shown.echoes = null;
      return;
    }
    panel.className = `path-panel ${kind}`;
    const path = me.path ?? {};
    const ember = kind === 'ember';
    $('path-ember-stats').hidden = !ember;
    $('rebirth-meter').hidden = !ember;
    const wardenRoom = (this.floor ?? 1) >= 5;
    if (kind === 'choosing') {
      setText('path-panel-title', path.choice ? label(PATHS, path.choice) : 'Eligiendo camino');
      setText('path-panel-sub', path.choice ? 'Elegido: se revela al resolver la ronda' : 'Ayudar o Vengarse');
      setText('path-panel-text', `Guardaste ${path.savedEmbers ?? 0} de oro. ${path.choice ? 'Espera la resolución.' : 'Si vence el plazo, eliges Ayudar.'}`);
    } else if (kind === 'renegade') {
      setText('path-panel-title', 'Traidor');
      setText('path-panel-sub', 'Del lado de la mazmorra');
      setText('path-panel-text', awaitingSpawn(me)
        ? this.spawnText(me)
        : 'Golpear a un héroe te da +1 de oro robado y dejarlo derribado +2. El primer golpe a cada héroe lo absorbe su escudo de aviso. Si todos caen, ganan los Traidores.');
    } else {
      const echoes = path.echoes ?? 0;
      const pct = rebirthPct(echoes);
      setText('path-panel-title', 'Fantasma');
      setText('path-panel-sub', wardenRoom ? 'Sala del Celador: puntúas al final' : 'Renaces al bajar de piso');
      setText('me-echoes', echoes);
      setText('me-saved', path.savedEmbers ?? 0);
      setText('me-rebirth', path.rebirthEmbers ?? Math.ceil((path.savedEmbers ?? 0) * pct / 100));
      // Eco nuevo: el número salta y flota el +N.
      const tile = $('tile-echoes');
      if (this.shown.echoes !== null && echoes > this.shown.echoes) {
        replay(tile, 'bump');
        const float = el('span', { class: 'float-delta', 'aria-hidden': 'true' }, `+${echoes - this.shown.echoes}`);
        tile.append(float);
        setTimeout(() => float.remove(), 1300);
      }
      this.shown.echoes = echoes;
      const cooldown = (id, tileId, cd, name) => {
        setText(id, cd > 0 ? cd : 'Lista');
        $(tileId).className = `tile tile-ability ${cd > 0 ? 'cooling' : 'ready'}`;
        $(tileId).title = cd > 0 ? `${name}: faltan ${cd} rondas` : `${name}: lista para usar`;
      };
      cooldown('me-kindle-cd', 'tile-kindle', me.abilityCooldown ?? 0, 'Escudo');
      cooldown('me-hinder-cd', 'tile-hinder', path.hinderCooldown ?? 0, 'Frenar');
      $('rebirth-fill').style.width = `${pct}%`;
      const meter = $('rebirth-meter');
      meter.setAttribute('aria-valuenow', String(pct));
      meter.title = `Recuperas el ${pct}% de tu oro guardado (25% + 8% por punto de apoyo, máx. 85%)`;
      setText('rebirth-label', `${pct}% de tu oro${pct >= 85 ? ' · máximo' : ` · +8% por punto de apoyo`}`);
      const ticks = $('rebirth-ticks');
      if (!ticks.childElementCount) {
        // Una marca por eco (25%, 33%… 81%) y el tope del 85%.
        for (let k = 0; k <= 7; k++) {
          const tick = el('i');
          tick.style.left = `${25 + 8 * k}%`;
          ticks.append(tick);
        }
        const cap = el('i', { class: 'cap' });
        cap.style.left = '85%';
        ticks.append(cap);
      }
      setText('path-panel-text', `Cada ayuda da puntos de apoyo. Con ${echoes} de apoyo recuperas el ${pct}% de tus ${path.savedEmbers ?? 0} de oro guardados.`);
    }
  }

  /** El registro de la ronda, agrupado por fase para seguir la resolución paso a paso. */
  eventItems(events) {
    const items = [];
    let phase;
    for (const ev of events) {
      if (ev.phase && ev.phase !== phase && PHASES[ev.phase]) {
        phase = ev.phase;
        items.push(el('li', { class: 'ev-phase' }, PHASES[ev.phase]));
      }
      items.push(this.eventItem(ev));
    }
    return items;
  }

  eventItem(ev) {
    const [icon, tone] = EVENT_STYLE[ev.type] ?? ['•', ''];
    let cls = tone;
    if (ev.type === 'DAMAGED') cls = ev.targetId === this.playerId ? 'bad' : ev.actorId === this.playerId ? 'good' : '';
    if (isSabotageHit(ev)) cls = 'sabotage';
    if (ev.type === 'BLOCKED' && ev.detail === 'WARNING_SHIELD') cls = 'renegade';
    if (ev.type === 'PATH_CHOSEN') cls = ev.detail === 'VENGEANCE' ? 'renegade' : 'ember';
    const classes = [cls ? `ev-${cls}` : '', ev.actorId === this.playerId ? 'ev-mine' : '', KEY_EVENTS.has(ev.type) || isSabotageHit(ev) ? 'ev-key' : ''].filter(Boolean).join(' ');
    return el('li', { class: classes }, el('span', { class: 'ev-ico', 'aria-hidden': 'true' }, isSabotageHit(ev) ? '🗡' : icon), el('span', {}, this.describeEvent(ev)));
  }

  /** Si sigue la ronda con un héroe activo sobre la escalera, es que aún quedan monstruos: se abre al terminar la ronda sin ellos. */
  onStairs() {
    const me = this.me;
    if (me?.state !== 'ALIVE' || me.hostEnemyId) return false;
    return [me.position, me.secondBody].some((p) => p && this.tiles.get(`${p.x},${p.y}`) === 'STAIRS');
  }

  renderOrderStatus() {
    const box = $('order-status');
    const show = (kind, main, tech) => {
      box.className = kind ? `order-status ${kind}` : 'order-status';
      box.replaceChildren(el('span', { class: 'os-main' }, main), ...(tech ? [el('small', { class: 'os-tech' }, tech)] : []));
    };
    const o = this.order;
    if (!o) {
      const pending = this.me?.pendingOrder;
      if (pending) show('ok', `✔ Orden registrada: ${describeOrder(pending)}`);
      else if (this.me && !canAct(this.me)) show('', `Ronda ${this.round || '–'}: no das órdenes por ahora.`);
      else if (this.status === 'IN_PROGRESS' && this.deadlineLocal !== null && this.onStairs()) show('warn', 'Aún quedan monstruos en la sala: la escalera se abre al eliminarlos todos.');
      else if (this.status === 'IN_PROGRESS' && this.deadlineLocal !== null) show('', `Ronda ${this.round}: elige tu orden.`);
      else show('', `Ronda ${this.round || '–'}: sin orden enviada.`);
      return;
    }
    const id = o.payload.clientActionId;
    const what = `Ronda ${o.payload.round}: ${describeOrder(o.payload.order)}`;
    const tech = `clientActionId …${id.slice(-12)} · intentos: ${o.attempts}`;
    if (o.status === 'acked') {
      const dup = o.ack.result === 'DUPLICATE_ACTION' ? ' (reenvío idempotente: el servidor ya la tenía)' : '';
      show('ok', `✔ ${what}`, `ACK ${o.ack.result}${dup} a las ${String(o.ack.receivedAt).slice(11, 19)} · ${tech}`);
    } else if (o.status === 'error') {
      show('bad', `✖ ${what} → rechazada: ${label(ERROR_CODES, o.error.code)}`, `${o.error.code}. ${o.error.message ?? ''} · ${tech}`);
    } else if (o.status === 'waiting') {
      show('pending', `${what} → sin confirmar; se reenviará al reconectar`, tech);
    } else {
      show('pending', `${what} → enviando…`, tech);
    }
  }

  renderTimer() {
    if (this.disposed) return;
    const bar = $('m-timerbar');
    const clock = $('m-clock');
    if (this.deadlineLocal === null) {
      setText('m-timer', '–');
      bar.style.width = '0%';
      clock.style.setProperty('--p', '0');
      clock.className = 'clock idle';
    } else {
      const left = Math.max(0, this.deadlineLocal - Date.now());
      setText('m-timer', left >= 10000 ? `${Math.ceil(left / 1000)}` : (left / 1000).toFixed(1));
      clock.style.setProperty('--p', String(Math.min(1, left / this.durationMs)));
      clock.className = left < 5000 && left > 0 ? 'clock low' : 'clock';
      if (left > 0 && left < 5000 && this.lowTimeRound !== this.round) {
        this.lowTimeRound = this.round;
        emit('round:low-time', { round: this.round, leftMs: left });
      }
      bar.style.width = `${Math.min(100, (left / this.durationMs) * 100)}%`;
      // Con algo planeado y sin pulsar Listo, se envía lo planeado justo antes del cierre (sin plan, el servidor defiende).
      if (left > 0 && left < AUTO_SEND_MS && this.planOrder() && this.planOpen() && this.autoSentRound !== this.round) {
        this.autoSentRound = this.round;
        emit('order:auto', { round: this.round });
        this.ready();
      }
      bar.className = left < 5000 ? 'low' : '';
    }
    this.renderBell();
    this.renderSignals();
    if (this.pactOffer && !this.draft) {
      const left = Math.max(0, Date.parse(this.pactOffer.deadline) - Date.now());
      setText('draft-timer', `${Math.ceil(left / 1000)} s`);
      $('draft-timer').classList.toggle('low', left < 5000);
    }
    if (this.draft) {
      const left = Math.max(0, Date.parse(this.draft.deadline) - Date.now());
      setText('draft-timer', `${Math.ceil(left / 1000)} s`);
      $('draft-timer').classList.toggle('low', left < 5000);
    }
    if (this.pathOffer) {
      const left = Math.max(0, this.pathOffer.deadlineLocal - Date.now());
      setText('path-timer', `${Math.ceil(left / 1000)} s`);
      $('path-timer').classList.toggle('low', left < 5000);
      const bar = $('path-timebar');
      bar.style.width = `${Math.min(100, (left / PATH_DEADLINE_MS) * 100)}%`;
      bar.classList.toggle('low', left < 5000);
    }
  }

  describeEvent(ev) {
    const who = (id) => {
      if (!id) return '';
      if (id === this.playerId) return `${this.me?.displayName ?? 'Tú'} (tú)`;
      if (id.startsWith('enemy:')) {
        const enemy = this.enemies.get(Number(id.slice(6)));
        return enemy ? `${label(ENEMIES, enemy.kind)} #${id.slice(6)}` : `Enemigo #${id.slice(6)}`;
      }
      return this.allies.find((a) => a.playerId === id)?.displayName ?? 'Alguien';
    };
    if (isSabotageHit(ev)) return `¡${who(ev.targetId)} recibió ${ev.amount ?? '?'} de daño y nadie vio de quién!`;
    if (ev.type === 'DAMAGED' && ev.targetId) {
      return `${who(ev.targetId)} recibió ${ev.amount ?? '?'} de daño${ev.actorId ? ` de ${who(ev.actorId)}` : ''}`;
    }
    const path = (p) => label(PATHS, String(p ?? '').replace(/_AUTO$/, ''));
    const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
    switch (ev.type) {
      case 'EXTINGUISHED':
        return `${who(ev.targetId ?? ev.actorId)} murió${ev.amount ? ` (${ev.amount} de oro)` : ''}`;
      case 'PATH_OFFERED':
        return `Moriste: elige camino (guardaste ${plural(ev.amount ?? 0, 'de oro', 'de oro')})`;
      case 'PATH_SELECTED':
        return String(ev.detail ?? '').endsWith('_AUTO')
          ? `Venció el plazo: tomaste el ${path(ev.detail)}`
          : `Tu elección quedó registrada: ${path(ev.detail)}`;
      case 'PATH_CHOSEN':
        return `${who(ev.actorId)} eligió ${path(ev.detail)}${ev.detail === 'VENGEANCE' ? ': volverá como Traidor' : ': ahora es un Fantasma'}`;
      case 'EMBER_REBORN':
        return `${who(ev.actorId)} renació en la escalera con ${ev.amount ?? '?'} de vida`;
      case 'RENEGADE_SPAWNED':
        return `${who(ev.actorId)} apareció como Traidor${ev.position ? ` en (${ev.position.x},${ev.position.y})` : ''}: cada héroe gana +1 de escudo`;
      case 'RENEGADE_DEFEATED':
        return ev.actorId
          ? `${who(ev.actorId)} derrotó al Traidor ${who(ev.targetId)} (+2 de oro)`
          : `El Traidor ${who(ev.targetId)} fue derrotado`;
      case 'ECHO_GAINED':
        return `+${plural(ev.amount ?? 1, 'punto de apoyo', 'puntos de apoyo')}: ${ECHO_REASONS[ev.detail] ?? 'ayudaste al grupo'}`;
      case 'SHIELD_KINDLED':
        return ev.targetId ? `${who(ev.actorId)} dio escudo a ${who(ev.targetId)} (+1 de escudo)` : `${who(ev.actorId)} intentó dar escudo, pero no había nadie`;
      case 'ATTACK_HINDERED':
        return ev.targetId
          ? `${who(ev.actorId)} frenó a ${who(ev.targetId)}: su golpe se retrasa una ronda`
          : `${who(ev.actorId)} intentó frenar, sin efecto`;
      case 'BEACON_LIT':
        return `${who(ev.actorId)} encendió una Luz${ev.position ? ` en (${ev.position.x},${ev.position.y})` : ''}`;
      case 'WARMTH_HEALED':
        return `La calidez de ${who(ev.actorId)} curó a ${who(ev.targetId)}`;
      case 'ECHO_FIRED':
        return `Disparó un Eco de ${who(ev.actorId)}${ev.position ? ` desde (${ev.position.x},${ev.position.y})` : ''}`;
      case 'ATTACK_CORRECTED':
        return ev.targetId ? `El ataque marcado de ${who(ev.targetId)} se desvió: nadie sabe quién fue` : 'Un golpe marcado se desvió: nadie sabe quién fue';
      case 'BETRAYAL_RECORDED':
        return `En secreto, le costaste ${ev.amount ?? '?'} de vida a ${who(ev.targetId)}`;
      case 'PUSHED':
        return `${who(ev.actorId)} empujó a ${who(ev.targetId)}${ev.position ? ` hasta (${ev.position.x},${ev.position.y})` : ''}`;
      case 'ROOM_FLOODED':
        return `El agua sube: ${plural(ev.amount ?? 1, 'fila inundada', 'filas inundadas')}`;
      case 'CHILLED':
        return `El frío: ${ev.amount ?? 1} de daño a cada uno, sin defensa posible`;
      case 'FLOOR_ENTERED': {
        const room = ROOM_TYPES[ev.detail];
        return `Entraron a la sala ${ev.amount ?? ''}${room && ev.detail !== 'NORMAL' ? `: ${room.name}. ${room.effect ?? ''}` : ''}`;
      }
      case 'EMBERS_GAINED':
        if (ev.detail === 'ASSIST') return `${who(ev.actorId)}: asistencia, +${ev.amount ?? 1} de oro`;
        if (ev.detail === 'WARDEN_BOUNTY') return `${who(ev.actorId)}: reparto del Celador, +${ev.amount ?? 1} de oro`;
        break;
      case 'BETRAYAL_REVEALED':
        return `${who(ev.actorId)} le costó ${ev.amount ?? '?'} de vida a ${who(ev.targetId)}`;
      case 'HOST_BOARDED':
        return `Entraste en ${who(ev.targetId)}: nadie te ve`;
      case 'HOST_EXITED':
        return `${who(ev.actorId)} reapareció${ev.position ? ` en (${ev.position.x},${ev.position.y})` : ''}`;
      case 'HOST_DRAINED':
        return `Drenaste a ${who(ev.targetId)}`;
      case 'WARDEN_TURNED':
        return `${who(ev.targetId) || 'El Celador'} giró${ev.detail ? ` hacia el ${label(DIRECTIONS, ev.detail)}` : ''}: ahora se le puede golpear de espaldas`;
      case 'SHOUTED':
        return `${who(ev.actorId)} gritó: su bando ve toda la sala y los monstruos van por ella`;
      case 'DEBT_CHANGED': {
        const delta = ev.amount ?? 0;
        return `Deuda de ${who(ev.targetId)} con ${who(ev.actorId)}: ${delta > 0 ? '+' : ''}${delta} (${DEBT_REASONS[ev.detail] ?? 'cambio'})`;
      }
      case 'EMBERS_DROPPED':
        return `Cayeron ${plural(ev.amount ?? 1, 'de oro', 'de oro')} al suelo${ev.position ? ` en (${ev.position.x},${ev.position.y})` : ''}`;
      case 'EMBERS_PICKED_UP':
        return `${who(ev.actorId)} recogió ${plural(ev.amount ?? 1, 'de oro', 'de oro')} del suelo`;
      case 'EMBERS_LOST':
        if (ev.detail === 'TRIAL_PENALTY') return `Te condenaron: perdiste ${Math.abs(ev.amount ?? 3)} de oro`;
        if (ev.detail === 'TRIAL_FINE') return `Acusaste a un inocente: perdiste ${Math.abs(ev.amount ?? 1)} de oro`;
        return `Perdiste ${plural(Math.abs(ev.amount ?? 1), 'de oro', 'de oro')}`;
      case 'ENEMY_BRIBED':
        return `${who(ev.actorId)} sobornó a ${who(ev.targetId)} con ${plural(ev.amount ?? 1, 'de oro', 'de oro')}`;
      case 'ROOM_SHIFTED': {
        const shift = shiftFromEvent(ev);
        return shift ? `${who(ev.actorId)}: ${shiftText(shift).toLowerCase()}` : `${who(ev.actorId)} corrió la sala`;
      }
      case 'BELL_RUNG':
        return `${who(ev.actorId)} tocó el Repique: en la próxima ronda los demás tienen solo 8 s`;
      case 'BELL_EXPIRED':
        return `Venció el plazo del Repique de ${who(ev.actorId)}`;
      case 'BLOCKED':
        if (ev.detail === 'WARNING_SHIELD') return `El escudo de aviso de ${who(ev.targetId)} absorbió el golpe del Traidor ${who(ev.actorId)}`;
        if (ev.targetId) return `${who(ev.targetId)} bloqueó el golpe${ev.actorId ? ` de ${who(ev.actorId)}` : ''}`;
        break;
      case 'REVIVED':
        return ev.actorId ? `${who(ev.actorId)} levantó a ${who(ev.targetId)}` : `${who(ev.targetId)} fue levantado`;
      case 'HEALED':
        return `${who(ev.targetId)} se curó${ev.amount ? ` ${ev.amount}` : ''}`;
      default:
        break;
    }
    const parts = [who(ev.actorId), label(EVENTS, ev.type)];
    if (ev.targetId) parts.push(`→ ${who(ev.targetId)}`);
    if (ev.amount !== undefined) parts.push(`(${ev.amount})`);
    if (ev.position) parts.push(`en (${ev.position.x},${ev.position.y})`);
    return parts.filter(Boolean).join(' ');
  }
}

const isBlindMe = (me) => me?.hero === 'ESCUCHA' && me?.state !== 'EMBER_SPIRIT';

/** Nombres de los botones de orden según el héroe; dentro de un huésped, lo que hace cada orden allí. */
function heroOrders(me) {
  const info = heroInfo(me?.hero);
  if (me?.hostEnemyId != null) {
    return {
      MOVE: { name: 'Mover huésped', icon: '➜', text: 'mover lo mueve a su velocidad.' },
      ATTACK: { name: 'Golpear', icon: '⚔', text: 'golpea ya, sin marcar, con su daño y su alcance. Si le das a un aliado, nadie sabrá que fuiste tú.' },
      ABILITY: { name: 'Salir', icon: '◉', text: 'sales a esa casilla o a la libre más cercana, y el huésped no actúa esta ronda.' },
      INTERACT: { name: 'Usar', icon: '✋', text: 'dentro de un huésped no hace nada.' },
      GUARD: { name: 'Defender', icon: '⛨', text: 'dentro de un huésped no hace nada.' },
    };
  }
  return {
    ...HERO_ORDERS,
    ATTACK: me?.hero === 'ESCUCHA' && me.state !== 'RENEGADE' ? { name: 'A tientas', icon: '⚔' } : HERO_ORDERS.ATTACK,
    ABILITY: { name: info.ability || 'Habilidad', icon: '✺' },
  };
}

function describeOrder(order) {
  const action = order.direction
    ? `${label(ORDER_TYPES, order.type)} ${label(DIRECTIONS, order.direction)}`
    : label(ORDER_TYPES, order.type);
  return order.step ? `Paso ${label(DIRECTIONS, order.step)} y ${action.toLowerCase()}` : action;
}

function showFinished(m, myPlayerId, onBack) {
  showScreen('finished');
  const iWon = m.winners.includes(myPlayerId);
  const lostAll = m.outcome === 'EMBER_EXTINGUISHED';
  // «El Umbral reclama»: todos los héroes cayeron y ganan los Renegados en pie. Tiene su propio tono, gane quien gane.
  const threshold = m.outcome === 'THRESHOLD_CLAIMED';
  document.querySelector('#screen-finished .fin').classList.toggle('threshold-claimed', threshold);
  $('fin-banner').className = `fin-banner${threshold ? ' threshold' : ''}${iWon ? ' victory' : lostAll ? ' defeat' : ''}`;
  setText('fin-emblem', threshold ? '☠' : iWon ? '♛' : lostAll ? '🕯' : '✦');
  setText('fin-title', threshold ? (iWon ? '¡Ganan los Traidores… y tú con ellos!' : 'Ganan los Traidores') : iWon ? '¡Victoria!' : label(OUTCOMES, m.outcome));
  let verdict = iWon ? label(OUTCOMES, m.outcome) : m.winners.length ? 'Otro portador se quedó con el fuego.' : 'Sin ganador.';
  if (threshold) {
    verdict = iWon
      ? 'Todos los héroes cayeron: ganaste como Traidor'
      : `Todos los héroes cayeron: ${m.winners.length > 1 ? 'los Traidores se quedan' : 'el Traidor se queda'} con el oro`;
  }
  setText('fin-summary', `${verdict} · ${m.rounds} rondas · terminó a las ${String(m.finishedAt).slice(11, 19)} UTC`);

  const sorted = [...m.standings].sort((a, b) => a.placement - b.placement);
  const isWinner = (s) => m.winners.includes(s.playerId);
  const you = (s) => (s.playerId === myPlayerId ? ' (tú)' : '');

  $('fin-podium').replaceChildren(...sorted.slice(0, 3).map((s, i) => el('li', { class: [`p${i + 1}`, pathClass(s.finalState)].filter(Boolean).join(' '), dataset: { hero: s.hero } },
    isWinner(s) ? el('span', { class: 'crown', 'aria-label': 'Ganador' }, '♛') : null,
    heroAvatar(s.hero, i === 0 ? 'xl' : 'lg'),
    el('b', {}, `${s.displayName}${you(s)}`),
    el('span', { class: 'em' }, `✦ ${s.embers}`),
    el('span', { class: 'step', 'aria-hidden': 'true' }, String(s.placement)))));

  $('fin-standings').replaceChildren(...sorted.map((s, i) => {
    const stateIcon = { EMBER_SPIRIT: '❂ ', RENEGADE: '☠ ', EXTINCT: '🕯 ' }[s.finalState] ?? '';
    const li = el('li', { class: [s.playerId === myPlayerId ? 'me' : '', isWinner(s) ? 'winner' : '', pathClass(s.finalState)].filter(Boolean).join(' '), dataset: { hero: s.hero } },
      el('span', { class: 'place' }, `${s.placement}${isWinner(s) ? '♛' : ''}`),
      heroAvatar(s.hero, 'sm'),
      el('span', { class: 's-who' }, el('b', {}, `${s.displayName}${you(s)}`),
        el('small', {}, `${label(HEROES, s.hero)} · ${stateIcon}${label(HERO_STATES, s.finalState)}`)),
      el('span', { class: 'em' }, `${s.embers} `, el('small', {}, 'de oro')));
    li.style.animationDelay = `${0.9 + i * 0.08}s`;
    return li;
  }));

  const byId = new Map(m.standings.map((s) => [s.playerId, s]));

  // Maldiciones con las que se jugó.
  const curses = m.curses ?? [];
  $('fin-curses').hidden = !curses.length;
  $('fin-curses').textContent = curses.length ? `Maldiciones: ${curses.map((c) => `${CURSES[c]?.icon ?? '☠'} ${CURSES[c]?.name ?? c}`).join(' · ')}` : '';

  // Títulos: los calcula el servidor con datos reales; un empate da una fila por jugador.
  const awards = m.awards ?? [];
  $('fin-awards-box').hidden = !awards.length;
  $('fin-awards').replaceChildren(...awards.map((a, i) => {
    const who = byId.get(a.playerId);
    const li = el('li', { dataset: who ? { hero: who.hero } : {} },
      who ? heroAvatar(who.hero, 'sm') : null,
      el('span', {}, el('b', { class: 'award-title' }, a.title), el('small', {}, `${who?.displayName ?? 'Alguien'}${who ? you(who) : ''} · ${a.reason}`)));
    li.style.animationDelay = `${0.9 + i * 0.1}s`;
    return li;
  }));

  // Traiciones anónimas: aquí se sabe quién fue («Errata le costó 3 de vida a Ana»).
  const betrayals = (m.betrayals ?? []).filter((b) => b.damage > 0);
  $('fin-betrayal-box').hidden = !betrayals.length;
  $('fin-betrayals').replaceChildren(...betrayals.map((b, i) => {
    const traitor = byId.get(b.traitorId);
    const victim = byId.get(b.victimId);
    const li = el('li', { dataset: traitor ? { hero: traitor.hero } : {} },
      traitor ? heroAvatar(traitor.hero, 'sm') : null,
      el('span', {}, el('b', {}, label(HEROES, traitor?.hero ?? '—')), ` le costó ${b.damage} de vida a `, el('b', {}, `${victim?.displayName ?? 'alguien'}${victim ? you(victim) : ''}`),
        traitor ? el('small', {}, ` · la jugaba ${traitor.displayName}${you(traitor)}`) : null));
    li.style.animationDelay = `${1 + i * 0.2}s`;
    return li;
  }));

  // Pactos revelados: cartas boca abajo que se voltean una a una (clic o Enter para voltear de nuevo).
  const withPact = sorted.filter((s) => s.pact);
  $('fin-pacts').replaceChildren(...withPact.map((s, i) => {
    const done = Boolean(s.pact.fulfilled);
    const card = el('div', { class: 'flip', tabindex: 0, role: 'button', 'aria-pressed': 'false', 'aria-label': `Misión de ${s.displayName}: ${s.pact.description}. ${done ? 'Cumplido' : 'No cumplido'}` },
      el('div', { class: 'flip-inner' },
        el('div', { class: 'flip-face flip-front', 'aria-hidden': 'true' },
          el('span', { class: 'seal' }, '✶'), el('b', {}, `Misión de ${s.displayName}`), el('small', {}, 'secreta')),
        el('div', { class: `flip-face flip-back ${done ? 'done' : 'failed'}`, 'aria-hidden': 'true' },
          el('span', { class: 'who' }, `${s.displayName}${you(s)}`),
          el('p', {}, s.pact.description),
          el('span', { class: 'verdict' }, done ? '✔ Cumplido (+3 de oro)' : '✖ No cumplido'))));
    const flip = () => {
      card.classList.toggle('revealed');
      card.setAttribute('aria-pressed', String(card.classList.contains('revealed')));
    };
    card.addEventListener('click', flip);
    card.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); flip(); } });
    setTimeout(() => { if (!card.classList.contains('revealed')) flip(); }, 1400 + i * 450);
    return card;
  }));
  if (!withPact.length) $('fin-pacts').replaceChildren(el('p', { class: 'hint' }, 'Nadie tenía una misión que revelar.'));

  const back = $('btn-back-lobby');
  back.onclick = () => onBack();
}
