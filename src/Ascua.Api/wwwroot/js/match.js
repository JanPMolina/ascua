// Pantalla de partida: estado de la vista, mensajes del hub, órdenes, reconexión y fin.
// Funciona igual con el transporte real (hub.js) y con el de la demo (demo.js).
import { $, el, setText, showScreen, toast } from './ui.js';
import { BoardRenderer } from './board.js';
import { logMessage, setLastSeq } from './log.js';
import { NO_RECONNECT } from './hub.js';
import { uuidv7 } from './uuid.js';
import {
  ABILITIES, BLESSINGS, DIRECTIONS, ENEMIES, ERROR_CODES, EVENTS, HEROES, HERO_STATES,
  MATCH_STATUS, ORDER_TYPES, OUTCOMES, label,
} from './labels.js';

const KEY_DIRS = { ArrowUp: 'N', ArrowDown: 'S', ArrowLeft: 'W', ArrowRight: 'E' };
const RETRYABLE = new Set(['RATE_LIMITED', 'INTERNAL_ERROR']);
const FATAL_JOIN = new Set(['NOT_PARTICIPANT', 'MATCH_NOT_FOUND', 'MATCH_NOT_ACTIVE', 'ALREADY_IN_ANOTHER_MATCH']);

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
    listen($('btn-guard'), 'click', () => this.submitOrder({ type: 'GUARD' }));
    listen($('btn-leave-match'), 'click', () => this.leave());
    listen($('btn-demo-drop'), 'click', () => toast(this.transport.simulateDrop()));
    listen($('btn-demo-finish'), 'click', () => this.transport.simulateFinish());
  }

  // ───────────── conexión ─────────────

  setConn(text, kind = '') {
    const badge = $('conn-state');
    badge.textContent = `${text} (${this.transport.protocolName})`;
    badge.className = `badge ${kind}`;
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
  }

  onPlayerDecided(m) {
    if (m.round !== this.round) return;
    this.decidedCount = m.decidedCount;
    this.expectedPlayers = m.expectedPlayers;
    if (this.me && m.playerId === this.playerId) this.me.decided = true;
    const ally = this.allies.find((a) => a.playerId === m.playerId);
    if (ally) ally.decided = true;
  }

  onRoundResolved(m) {
    if (this.seq !== m.previousSeq) {
      // Hueco de secuencia: se descarta el delta y se pide la vista completa (contrato §4).
      logMessage('sys', 'SeqGap', { expected: this.seq, previousSeq: m.previousSeq });
      this.requestSnapshot();
      return;
    }
    const d = m.delta;
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
  }

  onSnapshot(m) {
    this.applySnapshot(m);
  }

  onDraftOffer(m) {
    this.draft = { ...m, deadlineLocal: Date.now() + Math.max(0, Date.parse(m.deadline) - Date.now()) };
    const box = $('draft-options');
    box.replaceChildren(...m.options.map((id) => el('button', {
      type: 'button',
      onclick: () => this.pickBlessing(id),
    }, label(BLESSINGS, id))));
    setText('draft-source', m.source === 'ALTAR' ? 'Altar' : 'Draft inicial');
    $('draft').hidden = false;
  }

  async pickBlessing(blessingId) {
    if (!this.draft) return;
    const { offerId } = this.draft;
    $('draft-options').querySelectorAll('button').forEach((b) => { b.disabled = true; });
    try {
      const res = await this.invoke('DraftPick', { matchId: this.matchId, offerId, blessingId });
      if (res.error && res.error.code !== 'DRAFT_CLOSED') {
        toast(`DraftPick: ${label(ERROR_CODES, res.error.code)}`, 'bad');
        $('draft-options').querySelectorAll('button').forEach((b) => { b.disabled = false; });
        return;
      }
      if (!res.error) toast(`Elegiste ${label(BLESSINGS, res.blessingId)}`);
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
    if (this.disposed || $('screen-match').hidden || !$('draft').hidden) return;
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
      case ' ': e.preventDefault(); this.submitOrder({ type: 'GUARD' }); return;
      default: return;
    }
    this.render();
  }

  sendDirected(type, direction) {
    this.armedMode = null;
    this.submitOrder({ type, direction });
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
          if (res.error.code === 'STALE_ROUND' && res.error.details?.currentRound) this.round = res.error.details.currentRound;
        }
      } else {
        o.status = 'acked';
        o.ack = res;
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
    setText('m-status', label(MATCH_STATUS, this.status ?? '–'));

    // Héroe propio.
    if (me) {
      setText('me-title', `${me.displayName} — ${label(HEROES, me.hero)} · habilidad: ${label(ABILITIES, me.hero)}`);
      const pct = Math.max(0, Math.min(100, (me.hp / me.maxHp) * 100));
      const hpbar = $('me-hpbar');
      hpbar.style.width = `${pct}%`;
      hpbar.className = pct <= 34 ? 'bar-hp low' : 'bar-hp';
      setText('me-hp', `Vida ${me.hp}/${me.maxHp}${me.shield ? ` + escudo ${me.shield}` : ''}`);
      setText('me-shield', me.shield);
      setText('me-embers', me.embers);
      setText('me-charges', me.charges);
      setText('me-cooldown', me.abilityCooldown);
      setText('me-stealth', me.stealthRoundsLeft);
      const downNote = me.state === 'DOWN' && me.downRoundsLeft !== null ? ` (${me.downRoundsLeft} rondas)` : '';
      setText('me-state', `${label(HERO_STATES, me.state)}${downNote}`);
      $('me-blessings').replaceChildren(...(me.blessings.length
        ? me.blessings.map((b) => el('li', {}, label(BLESSINGS, b)))
        : [el('li', {}, 'ninguna')]));
      setText('me-pact', me.pact ? me.pact.description : 'sin pacto');
    }

    // Jugadores y quién decidió.
    const players = me ? [{ ...me, connected: this.transport.connected, isMe: true }, ...this.allies] : this.allies;
    $('players').replaceChildren(...players.map((p) => el('li', {},
      el('span', { class: `dot ${p.connected ? 'on' : 'off'}`, title: p.connected ? 'conectado' : 'desconectado' }),
      el('span', { class: 'who' }, `${p.displayName}${p.isMe ? ' (tú)' : ''} · ${label(HEROES, p.hero).split(' ')[0]}`),
      el('span', {}, `♥${p.hp}/${p.maxHp} · ✦${p.embers}`),
      el('span', { class: p.decided ? 'badge ok' : 'badge' },
        p.state !== 'ALIVE' ? label(HERO_STATES, p.state) : p.decided ? 'decidió' : 'pensando'))));
    setText('m-decided', this.status === 'IN_PROGRESS' && this.expectedPlayers
      ? `${this.decidedCount}/${this.expectedPlayers} decidieron` : '');

    // Modo de orden.
    const mode = this.armedMode ?? this.selectedMode;
    document.querySelectorAll('.mode').forEach((b) => b.classList.toggle('active', b.dataset.mode === mode));
    this.renderOrderStatus();

    // Eventos de la última ronda.
    $('events').replaceChildren(...(this.events.length
      ? this.events.map((ev) => el('li', {}, this.describeEvent(ev)))
      : [el('li', {}, '—')]));

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

  renderOrderStatus() {
    const box = $('order-status');
    const o = this.order;
    if (!o) {
      const pending = this.me?.pendingOrder;
      box.className = pending ? 'order-status ok' : 'order-status';
      box.textContent = pending ? `Orden registrada: ${describeOrder(pending)}` : `Ronda ${this.round || '–'}: sin orden enviada.`;
      return;
    }
    const id = o.payload.clientActionId;
    const head = `Ronda ${o.payload.round}: ${describeOrder(o.payload.order)} · clientActionId …${id.slice(-12)}`;
    if (o.status === 'acked') {
      box.className = 'order-status ok';
      const dup = o.ack.result === 'DUPLICATE_ACTION' ? ' (reenvío idempotente: el servidor ya la tenía)' : '';
      box.textContent = `${head} → ACK ${o.ack.result}${dup} a las ${String(o.ack.receivedAt).slice(11, 19)} (intentos: ${o.attempts})`;
    } else if (o.status === 'error') {
      box.className = 'order-status bad';
      box.textContent = `${head} → rechazada: ${o.error.code} — ${label(ERROR_CODES, o.error.code)}. ${o.error.message ?? ''}`;
    } else if (o.status === 'waiting') {
      box.className = 'order-status pending';
      box.textContent = `${head} → sin ACK; se reenviará con el mismo id al reconectar (intentos: ${o.attempts})`;
    } else {
      box.className = 'order-status pending';
      box.textContent = `${head} → enviando…`;
    }
  }

  renderTimer() {
    if (this.disposed) return;
    const bar = $('m-timerbar');
    if (this.deadlineLocal === null) {
      setText('m-timer', '–');
      bar.style.width = '0%';
    } else {
      const left = Math.max(0, this.deadlineLocal - Date.now());
      setText('m-timer', `${(left / 1000).toFixed(1)} s`);
      bar.style.width = `${Math.min(100, (left / this.durationMs) * 100)}%`;
      bar.className = left < 5000 ? 'low' : '';
    }
    if (this.draft) {
      const left = Math.max(0, Date.parse(this.draft.deadline) - Date.now());
      setText('draft-timer', `${Math.ceil(left / 1000)} s`);
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
  setText('fin-title', label(OUTCOMES, m.outcome));
  const iWon = m.winners.includes(myPlayerId);
  setText('fin-summary', `${m.rounds} rondas · ${iWon ? '¡Ganaste!' : m.winners.length ? 'No ganaste esta vez.' : 'Sin ganador.'}`
    + ` · terminó a las ${String(m.finishedAt).slice(11, 19)} UTC`);
  $('fin-standings').replaceChildren(...[...m.standings].sort((a, b) => a.placement - b.placement).map((s) => el('tr', {},
    el('td', {}, `${s.placement}${m.winners.includes(s.playerId) ? ' 🏆' : ''}`),
    el('td', {}, `${s.displayName}${s.playerId === myPlayerId ? ' (tú)' : ''}`),
    el('td', {}, label(HEROES, s.hero)),
    el('td', {}, label(HERO_STATES, s.finalState)),
    el('td', {}, String(s.embers)),
    el('td', {}, s.pact ? s.pact.description : '—'),
    el('td', {}, s.pact ? (s.pact.fulfilled ? 'sí' : 'no') : '—'))));
  const back = $('btn-back-lobby');
  back.onclick = () => onBack();
}
