// Pantalla de partida: estado de la vista, mensajes del hub, órdenes, reconexión y fin.
// Funciona igual con el transporte real (hub.js) y con el de la demo (demo.js).
import { $, el, setText, showScreen, toast } from './ui.js';
import { BoardRenderer } from './board.js';
import { logMessage, setLastSeq } from './log.js';
import { NO_RECONNECT } from './hub.js';
import { uuidv7 } from './uuid.js';
import { emit } from './bus.js';
import { BLESSING_ICONS, heroAvatar, heroInfo } from './heroes.js';
import { settingsOpen } from './settings-panel.js';
import {
  BLESSINGS, BLESSING_EFFECTS, DIRECTIONS, ENEMIES, ERROR_CODES, EVENTS, HEROES, HERO_STATES,
  MATCH_STATUS, ORDER_TYPES, OUTCOMES, label,
} from './labels.js';

const KEY_DIRS = { ArrowUp: 'N', ArrowDown: 'S', ArrowLeft: 'W', ArrowRight: 'E' };
const RETRYABLE = new Set(['RATE_LIMITED', 'INTERNAL_ERROR']);
const FATAL_JOIN = new Set(['NOT_PARTICIPANT', 'MATCH_NOT_FOUND', 'MATCH_NOT_ACTIVE', 'ALREADY_IN_ANOTHER_MATCH']);

/** Ícono y tono de cada evento en el registro de la ronda ('' = neutro). */
const EVENT_STYLE = {
  MOVED: ['👣', ''], BOUNCED: ['↩', ''], ATTACKED: ['⚔', ''], DAMAGED: ['💥', ''], BLOCKED: ['⛨', 'info'],
  HEALED: ['✚', 'good'], DOWNED: ['☠', 'bad'], REVIVED: ['✋', 'good'], EXTINGUISHED: ['🕯', 'bad'],
  ENEMY_KILLED: ['💀', 'gold'], ENEMY_SPAWNED: ['👁', 'bad'], CHEST_OPENED: ['📦', 'gold'], EMBERS_GAINED: ['✦', 'gold'],
  EMBERS_STOLEN: ['✦', 'bad'], BLESSING_GAINED: ['✧', 'gold'], TRAP_TRIGGERED: ['⚠', 'bad'], FIRE_STARTED: ['🔥', 'bad'],
  FROZEN: ['❄', 'info'], BOSS_PHASE_CHANGED: ['👑', 'bad'], FLOOR_ENTERED: ['⇣', 'info'], DEFAULT_GUARD_APPLIED: ['⛨', ''],
};

const ARMED_TEXT = {
  ABILITY: 'Habilidad armada: elige una dirección (flecha o d-pad). Esc cancela.',
  INTERACT: 'Interactuar armado: elige la casilla vecina (flecha o d-pad). Esc cancela.',
};

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
export function startMatch({ transport, onExit }) {
  active?.dispose();
  active = new MatchController(transport, onExit);
  active.start();
  return active;
}

/** Corta la partida sin volver al lobby (p. ej. al perder la sesión). */
export function stopMatch() {
  active?.dispose();
  active = null;
}

class MatchController {
  constructor(transport, onExit) {
    this.transport = transport;
    this.matchId = transport.matchId;
    this.onExit = onExit;
    this.board = new BoardRenderer($('board'));
    this.disposed = false;
    this.finished = null;
    this.reconnectAttempt = 0;
    this.reconnectTimer = 0;
    this.drainDelayMs = null;
    this.selectedMode = 'MOVE';
    this.armedMode = null;
    this.order = null; // { payload, status: sending|waiting|acked|error, ack, error }
    this.draft = null;
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
    this.me = null;
    this.allies = [];
    this.enemies = new Map();
    this.tiles = new Map();
    this.deadlineLocal = null;
    this.durationMs = 20000;
    this.decidedCount = 0;
    this.expectedPlayers = 0;
    this.events = [];
    // Lo último que se pintó, para animar solo lo que cambia.
    this.shown = { hp: null, embers: null, blessings: null, pact: null, events: null, hero: null, decided: new Set() };
  }

  // ───────────── ciclo de vida ─────────────

  start() {
    showScreen('match');
    const demo = this.transport.protocolName === 'demo';
    $('btn-demo-drop').hidden = !demo;
    $('btn-demo-finish').hidden = !demo;
    this.bindUi();
    for (const type of ['RoundOpened', 'PlayerDecided', 'RoundResolved', 'Snapshot', 'DraftOffer',
      'PlayerConnectionChanged', 'MatchFinished', 'ServerDraining', 'Error']) {
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
    document.removeEventListener('keydown', this.onKey);
    this.cleanups.forEach((fn) => fn());
    this.transport.stop();
    this.transport.dispose?.();
    $('draft').hidden = true;
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
    document.querySelectorAll('.mode').forEach((btn) => listen(btn, 'click', () => {
      this.selectedMode = btn.dataset.mode;
      this.armedMode = null;
      this.render();
    }));
    document.querySelectorAll('.dir[data-dir]').forEach((btn) => listen(btn, 'click', () => {
      this.sendDirected(this.armedMode ?? this.selectedMode, btn.dataset.dir);
    }));
    listen($('btn-guard'), 'click', () => this.guard());
    listen($('btn-leave-match'), 'click', () => this.leave());
    listen($('btn-demo-drop'), 'click', () => toast(this.transport.simulateDrop()));
    listen($('btn-demo-finish'), 'click', () => this.transport.simulateFinish());
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
    this.floor = v.floor;
    this.me = v.me;
    this.allies = v.allies ?? [];
    this.enemies = new Map((v.enemies ?? []).map((e) => [e.enemyId, e]));
    this.tiles = new Map((v.tiles ?? []).map((t) => [`${t.x},${t.y}`, t.kind]));
    // El plazo se traduce al reloj local con el desfase que informa el servidor.
    if (v.roundDeadline) {
      const offset = Date.parse(snap.serverTime) - Date.now();
      this.deadlineLocal = Date.parse(v.roundDeadline) - (Number.isFinite(offset) ? offset : 0);
    } else {
      this.deadlineLocal = null;
    }
    this.decidedCount = [this.me, ...this.allies].filter((h) => h?.decided).length;
    this.expectedPlayers = [this.me, ...this.allies].filter((h) => h?.state === 'ALIVE').length;
    if (this.order && this.order.payload.round !== this.round) this.order = null;
    setLastSeq(this.seq);
  }

  onRoundOpened(m) {
    if (m.round < this.round) return;
    this.status = 'IN_PROGRESS';
    this.round = m.round;
    this.durationMs = m.durationMs;
    // Se usa la duración (no la hora absoluta) para no depender del reloj del cliente.
    this.deadlineLocal = Date.now() + m.durationMs;
    this.decidedCount = 0;
    this.expectedPlayers = m.expectedPlayers;
    if (this.me) { this.me.decided = false; this.me.pendingOrder = null; }
    this.allies.forEach((a) => { a.decided = false; });
    if (this.order && this.order.payload.round !== m.round) this.order = null;
    this.lowTimeRound = null;
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
    this.me = d.me;
    for (const ally of d.alliesChanged ?? []) {
      const i = this.allies.findIndex((a) => a.playerId === ally.playerId);
      if (i >= 0) this.allies[i] = ally; else this.allies.push(ally);
    }
    for (const enemy of d.enemiesUpserted ?? []) this.enemies.set(enemy.enemyId, enemy);
    for (const id of d.enemiesRemoved ?? []) this.enemies.delete(id);
    for (const t of d.tilesChanged ?? []) this.tiles.set(`${t.x},${t.y}`, t.kind);
    this.events = d.events ?? [];
    this.seq = m.seq;
    this.deadlineLocal = null;
    setLastSeq(this.seq);
    if (d.floorChanged) emit('floor:changed', { floor: d.floorChanged });
    emit('round:resolved', { round: m.round, events: this.events, before, after: this.entities(), myId: this.playerId });
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
    const box = $('draft-options');
    box.replaceChildren(...m.options.map((id) => el('button', {
      type: 'button',
      class: 'blessing',
      dataset: { blessing: id },
      onclick: () => this.pickBlessing(id),
    },
    el('span', { class: 'bl-ico', 'aria-hidden': 'true' }, BLESSING_ICONS[id] ?? '✧'),
    el('b', {}, label(BLESSINGS, id)),
    BLESSING_EFFECTS[id] ? el('small', {}, BLESSING_EFFECTS[id]) : null,
    el('span', { class: 'bl-cta', 'aria-hidden': 'true' }, 'Elegir'))));
    setText('draft-source', m.source === 'ALTAR' ? '⛩ Altar' : '✦ Draft inicial');
    $('draft').hidden = false;
    box.querySelector('button')?.focus();
    emit('draft:offer', { options: m.options, source: m.source });
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
      $('draft').hidden = true;
    } catch {
      $('draft-options').querySelectorAll('button').forEach((b) => { b.disabled = false; });
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
    $('draft').hidden = true;
    showFinished(m, this.playerId, () => this.exit());
    emit('match:finished', { outcome: m.outcome, iWon: m.winners.includes(this.playerId), standings: m.standings, myId: this.playerId });
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
    if (this.disposed || $('screen-match').hidden || !$('draft').hidden || !$('howto').hidden || settingsOpen()) return;
    const tag = document.activeElement?.tagName;
    if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return;
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    // Si un botón tiene el foco, Espacio también lo "pulsaría": se le quita el foco.
    if (tag === 'BUTTON' && (e.key === ' ' || KEY_DIRS[e.key])) document.activeElement.blur();
    const dir = KEY_DIRS[e.key];
    if (dir) {
      e.preventDefault();
      const type = e.shiftKey ? 'ATTACK' : this.armedMode ?? 'MOVE';
      this.sendDirected(type, dir);
      return;
    }
    switch (e.key.toLowerCase()) {
      case 'q': this.armedMode = this.armedMode === 'ABILITY' ? null : 'ABILITY'; break;
      case 'e': this.armedMode = this.armedMode === 'INTERACT' ? null : 'INTERACT'; break;
      case 'escape': this.armedMode = null; break;
      case ' ': e.preventDefault(); this.guard(); return;
      default: return;
    }
    this.render();
  }

  sendDirected(type, direction) {
    this.armedMode = null;
    const btn = document.querySelector(`.dir[data-dir="${direction}"]`);
    if (btn) replay(btn, 'pressed');
    this.submitOrder({ type, direction });
  }

  guard() {
    replay($('btn-guard'), 'pressed');
    this.submitOrder({ type: 'GUARD' });
  }

  submitOrder(order) {
    if (this.status !== 'IN_PROGRESS' || this.deadlineLocal === null) {
      toast('No hay una ronda abierta.');
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
    if (!window.confirm('¿Abandonar la partida? En curso cuenta como abandono y pierdes tus ascuas.')) return;
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

    // Héroe propio.
    if (me) this.renderMe(me);

    // Jugadores y quién decidió.
    const players = me ? [{ ...me, connected: this.transport.connected, isMe: true }, ...this.allies] : this.allies;
    const decidedNow = new Set();
    $('players').replaceChildren(...players.map((p) => {
      const info = heroInfo(p.hero);
      const alive = p.state === 'ALIVE';
      const pct = p.maxHp ? Math.max(0, Math.min(100, (p.hp / p.maxHp) * 100)) : 0;
      if (p.decided) decidedNow.add(p.playerId);
      let state;
      if (!alive) state = el('span', { class: 'badge bad p-state' }, label(HERO_STATES, p.state));
      else if (p.decided) {
        const fresh = !this.shown.decided.has(p.playerId);
        state = el('span', { class: `badge ok p-state${fresh ? ' decided' : ''}` }, '✔ decidió');
      } else {
        state = el('span', { class: 'badge p-state thinking' }, 'pensando',
          el('span', { class: 'dots', 'aria-hidden': 'true' }, el('i', {}, '.'), el('i', {}, '.'), el('i', {}, '.')));
      }
      const hp = el('span', { class: pct <= 34 ? 'mini-hp low' : 'mini-hp', 'aria-hidden': 'true' }, el('i'));
      hp.firstChild.style.width = `${pct}%`;
      return el('li', { class: [p.isMe ? 'me' : '', alive ? '' : 'down'].filter(Boolean).join(' '), dataset: { hero: p.hero } },
        el('span', { class: 'p-avatar' }, heroAvatar(p.hero, 'sm'),
          el('span', { class: `dot ${p.connected ? 'on' : 'off'}`, title: p.connected ? 'conectado' : 'desconectado' })),
        el('span', { class: 'who' }, p.displayName, p.isMe ? ' (tú)' : '', ' ', el('small', {}, `· ${info.name}`)),
        state,
        el('span', { class: 'p-stats' }, hp, el('span', {}, `♥ ${p.hp}/${p.maxHp}`), el('span', { class: 'em' }, `✦ ${p.embers}`)));
    }));
    this.shown.decided = decidedNow;
    setText('m-decided', this.status === 'IN_PROGRESS' && this.expectedPlayers
      ? `${this.decidedCount}/${this.expectedPlayers} decidieron` : '');
    $('m-decided').className = this.expectedPlayers && this.decidedCount >= this.expectedPlayers ? 'badge ok' : 'badge';

    // Modo de orden (resaltado distinto cuando Q o E esperan una dirección).
    const mode = this.armedMode ?? this.selectedMode;
    document.querySelectorAll('.mode').forEach((b) => {
      const on = b.dataset.mode === mode;
      b.classList.toggle('active', on && !this.armedMode);
      b.classList.toggle('armed', on && Boolean(this.armedMode));
      b.setAttribute('aria-checked', String(on));
    });
    const armed = $('order-armed');
    armed.hidden = !this.armedMode;
    armed.textContent = this.armedMode ? ARMED_TEXT[this.armedMode] ?? '' : '';
    document.querySelector('.dpad').classList.toggle('armed', Boolean(this.armedMode));
    this.renderOrderStatus();

    // Eventos de la última ronda (se rehacen solo cuando llega una ronda nueva, para no repetir la animación).
    if (this.shown.events !== this.events) {
      this.shown.events = this.events;
      $('events').replaceChildren(...(this.events.length
        ? this.events.map((ev) => this.eventItem(ev))
        : [el('li', { class: 'empty' }, 'Todavía no pasó nada en esta ronda.')]));
    }

    this.board.draw({
      tiles: this.tiles,
      me,
      allies: this.allies,
      enemies: this.enemies,
      pendingOrder: this.order?.status === 'acked' || this.order?.status === 'sending' || this.order?.status === 'waiting'
        ? this.order.payload.order : me?.pendingOrder ?? null,
      armedMode: this.armedMode,
    });
    this.renderTimer();
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
    setText('me-role', `${info.name} · ${info.role}`);

    // Vida: corazones + barra; si bajó, sacudida y corazones perdidos.
    const prev = this.shown.hp;
    const lost = prev !== null && me.hp < prev;
    const gained = prev !== null && me.hp > prev;
    this.shown.hp = me.hp;
    const hearts = [];
    for (let i = 0; i < me.maxHp; i++) {
      const full = i < me.hp;
      let cls = full ? 'heart' : 'heart empty';
      if (lost && i >= me.hp && i < prev) cls += ' lost';
      if (gained && i < me.hp && i >= prev) cls += ' gained';
      hearts.push(el('span', { class: cls }));
    }
    for (let i = 0; i < (me.shield ?? 0); i++) hearts.push(el('span', { class: 'heart shield' }));
    $('me-hearts').replaceChildren(...hearts);
    if (lost) replay(card, 'hit');
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

    // Cargas (pips), enfriamiento de la habilidad y sigilo.
    setText('me-charges', me.charges);
    $('me-charge-pips').replaceChildren(...Array.from({ length: Math.min(6, Math.max(me.charges, me.hero === 'CHISPA' ? 3 : 0)) },
      (_, i) => el('i', { class: i < me.charges ? 'on' : '' })));
    $('tile-charges').classList.toggle('dim', me.charges === 0 && me.hero !== 'CHISPA');
    const ready = !me.abilityCooldown;
    setText('me-cooldown', ready ? 'Lista' : me.abilityCooldown);
    setText('me-ability', info.ability || 'Habilidad');
    $('tile-ability').className = `tile tile-ability ${ready ? 'ready' : 'cooling'}`;
    $('tile-ability').title = ready ? `${info.ability}: lista para usar` : `${info.ability}: faltan ${me.abilityCooldown} rondas`;
    setText('me-stealth', me.stealthRoundsLeft);
    document.querySelector('.tile-stealth').classList.toggle('dim', !me.stealthRoundsLeft);

    const downNote = me.state === 'DOWN' && me.downRoundsLeft !== null ? ` (${me.downRoundsLeft} rondas)` : '';
    setText('me-state', `${label(HERO_STATES, me.state)}${downNote}`);
    $('me-state').className = me.state === 'ALIVE' ? 'badge ok' : me.state === 'DOWN' ? 'badge bad' : 'badge';

    // Bendiciones (chips con su efecto al pasar el cursor o con el foco) y pacto secreto.
    const blessingKey = me.blessings.join(',');
    if (this.shown.blessings !== blessingKey) {
      this.shown.blessings = blessingKey;
      $('me-blessings').replaceChildren(...(me.blessings.length
        ? me.blessings.map((b) => el('li', { tabindex: 0, 'data-tip': BLESSING_EFFECTS[b] ?? '', 'aria-label': `${label(BLESSINGS, b)}: ${BLESSING_EFFECTS[b] ?? ''}` },
          el('span', { 'aria-hidden': 'true' }, BLESSING_ICONS[b] ?? '✧'), label(BLESSINGS, b)))
        : [el('li', { class: 'none' }, 'ninguna todavía')]));
    }
    const pact = me.pact ? me.pact.description : 'sin pacto';
    if (this.shown.pact !== pact) {
      this.shown.pact = pact;
      $('me-pact').replaceChildren(el('p', {}, pact));
    }
  }

  eventItem(ev) {
    const [icon, tone] = EVENT_STYLE[ev.type] ?? ['•', ''];
    let cls = tone;
    if (ev.type === 'DAMAGED') cls = ev.targetId === this.playerId ? 'bad' : ev.actorId === this.playerId ? 'good' : '';
    const classes = [cls ? `ev-${cls}` : '', ev.actorId === this.playerId ? 'ev-mine' : ''].filter(Boolean).join(' ');
    return el('li', { class: classes }, el('span', { class: 'ev-ico', 'aria-hidden': 'true' }, icon), el('span', {}, this.describeEvent(ev)));
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
      bar.className = left < 5000 ? 'low' : '';
    }
    if (this.draft) {
      const left = Math.max(0, Date.parse(this.draft.deadline) - Date.now());
      setText('draft-timer', `${Math.ceil(left / 1000)} s`);
      $('draft-timer').classList.toggle('low', left < 5000);
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
    if (ev.type === 'DAMAGED' && ev.targetId) {
      return `${who(ev.targetId)} recibió ${ev.amount ?? '?'} de daño${ev.actorId ? ` de ${who(ev.actorId)}` : ''}`;
    }
    const parts = [who(ev.actorId), label(EVENTS, ev.type)];
    if (ev.targetId) parts.push(`→ ${who(ev.targetId)}`);
    if (ev.amount !== undefined) parts.push(`(${ev.amount})`);
    if (ev.position) parts.push(`en (${ev.position.x},${ev.position.y})`);
    return parts.filter(Boolean).join(' ');
  }
}

function describeOrder(order) {
  return order.direction
    ? `${label(ORDER_TYPES, order.type)} ${label(DIRECTIONS, order.direction)}`
    : label(ORDER_TYPES, order.type);
}

function showFinished(m, myPlayerId, onBack) {
  showScreen('finished');
  const iWon = m.winners.includes(myPlayerId);
  const lostAll = m.outcome === 'EMBER_EXTINGUISHED';
  $('fin-banner').className = `fin-banner${iWon ? ' victory' : lostAll ? ' defeat' : ''}`;
  setText('fin-emblem', iWon ? '♛' : lostAll ? '🕯' : '✦');
  setText('fin-title', iWon ? '¡Victoria!' : label(OUTCOMES, m.outcome));
  const verdict = iWon ? label(OUTCOMES, m.outcome) : m.winners.length ? 'Otro portador se quedó con el fuego.' : 'Sin ganador.';
  setText('fin-summary', `${verdict} · ${m.rounds} rondas · terminó a las ${String(m.finishedAt).slice(11, 19)} UTC`);

  const sorted = [...m.standings].sort((a, b) => a.placement - b.placement);
  const isWinner = (s) => m.winners.includes(s.playerId);
  const you = (s) => (s.playerId === myPlayerId ? ' (tú)' : '');

  $('fin-podium').replaceChildren(...sorted.slice(0, 3).map((s, i) => el('li', { class: `p${i + 1}`, dataset: { hero: s.hero } },
    isWinner(s) ? el('span', { class: 'crown', 'aria-label': 'Ganador' }, '♛') : null,
    heroAvatar(s.hero, i === 0 ? 'xl' : 'lg'),
    el('b', {}, `${s.displayName}${you(s)}`),
    el('span', { class: 'em' }, `✦ ${s.embers}`),
    el('span', { class: 'step', 'aria-hidden': 'true' }, String(s.placement)))));

  $('fin-standings').replaceChildren(...sorted.map((s, i) => {
    const li = el('li', { class: [s.playerId === myPlayerId ? 'me' : '', isWinner(s) ? 'winner' : ''].filter(Boolean).join(' '), dataset: { hero: s.hero } },
      el('span', { class: 'place' }, `${s.placement}${isWinner(s) ? '♛' : ''}`),
      heroAvatar(s.hero, 'sm'),
      el('span', { class: 's-who' }, el('b', {}, `${s.displayName}${you(s)}`),
        el('small', {}, `${label(HEROES, s.hero)} · ${label(HERO_STATES, s.finalState)}`)),
      el('span', { class: 'em' }, String(s.embers), el('small', {}, 'ascuas')));
    li.style.animationDelay = `${0.9 + i * 0.08}s`;
    return li;
  }));

  // Pactos revelados: cartas boca abajo que se voltean una a una (clic o Enter para voltear de nuevo).
  const withPact = sorted.filter((s) => s.pact);
  $('fin-pacts').replaceChildren(...withPact.map((s, i) => {
    const done = Boolean(s.pact.fulfilled);
    const card = el('div', { class: 'flip', tabindex: 0, role: 'button', 'aria-pressed': 'false', 'aria-label': `Pacto de ${s.displayName}: ${s.pact.description}. ${done ? 'Cumplido' : 'No cumplido'}` },
      el('div', { class: 'flip-inner' },
        el('div', { class: 'flip-face flip-front', 'aria-hidden': 'true' },
          el('span', { class: 'seal' }, '✶'), el('b', {}, `Pacto de ${s.displayName}`), el('small', {}, 'secreto')),
        el('div', { class: `flip-face flip-back ${done ? 'done' : 'failed'}`, 'aria-hidden': 'true' },
          el('span', { class: 'who' }, `${s.displayName}${you(s)}`),
          el('p', {}, s.pact.description),
          el('span', { class: 'verdict' }, done ? '✔ Cumplido (+4 ascuas)' : '✖ No cumplido'))));
    const flip = () => {
      card.classList.toggle('revealed');
      card.setAttribute('aria-pressed', String(card.classList.contains('revealed')));
    };
    card.addEventListener('click', flip);
    card.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); flip(); } });
    setTimeout(() => { if (!card.classList.contains('revealed')) flip(); }, 1400 + i * 450);
    return card;
  }));
  if (!withPact.length) $('fin-pacts').replaceChildren(el('p', { class: 'hint' }, 'Nadie tenía un pacto que revelar.'));

  const back = $('btn-back-lobby');
  back.onclick = () => onBack();
}
