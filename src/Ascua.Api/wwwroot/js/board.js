// Tablero 9×9 en <canvas>, animado con requestAnimationFrame.
//
// match.js entrega el estado lógico con draw(state) cada vez que cambia; el renderer guarda su propio
// estado visual (posiciones interpoladas, vida mostrada, transparencias) y lo dibuja a 60 fps solo
// mientras la pantalla de partida está visible. Al resolverse una ronda (`round:resolved` del bus)
// reproduce lo que pasó en orden lógico: movimientos de héroes → acciones de héroes → golpes anunciados
// de los enemigos → movimientos de enemigos → sucesos globales (fase del jefe, robos de ascuas).
//
// Solo se dibuja lo que vino en la vista: una casilla que el servidor nunca envió queda en niebla; una
// recordada pero fuera de la visión actual se ve desaturada. Lo táctico (ataques anunciados, vida, quién
// es quién) se dibuja encima de la iluminación y de la niebla para que se lea de un vistazo.
//
// Caminos tras la extinción: la Brasa errante es una llama azul translúcida sin barra de vida (drawSpirit), el
// Renegado lleva aura roja y marca de enemigo (drawEntity), y los Faros iluminan su zona hasta la resolución siguiente.
//
// Lo propio de cada uno de los nueve héroes (vistas previas, capas y efectos) está en board-heroes.js.
import { emit, on } from './bus.js';
import { getSettings } from './settings.js';
import { HERO_COLORS, dangerIcon, glow, grayCopy, makeCanvas, paintTile, sprite } from './sprites.js';
import { Floaters, Particles, Timeline, clamp01, ease } from './fx.js';
import { heroInfo } from './heroes.js';
import {
  applyHeroEvent, correctedTiles, drawEchoes, drawGroundEmbers, drawHiddenTelegraphs, drawHostMark, drawLastWord, drawNoise, drawRewind,
  drawShiftPreview, drawSignalMark, drawThreads, isBlind, mirrored, onShiftLine, shiftFromEvent, shiftFromOrder, shiftText, shiftedPos, signalFx, tensarCells,
} from './board-heroes.js';

export const SIZE = 9;

const DIRS = { N: [0, -1], S: [0, 1], E: [1, 0], W: [-1, 0] };
const DIR_NAMES = Object.keys(DIRS);
const MODE_COLORS = { MOVE: '#8fd0ff', ATTACK: '#ff6a5a', ABILITY: '#e8c15a', INTERACT: '#6cc28a', GUARD: '#8fd0ff', SABOTAGE: '#c58af0', PUSH: '#ffb45a' };
/** Lo que no deja dar un paso (los cofres ya abiertos tampoco se pisan). */
const STOPS_STEP = new Set(['WALL', 'BRAZIER', 'ALTAR', 'CHEST', 'CHEST_OPENED']);
const STOPS_SHOTS = new Set(['WALL', 'BRAZIER']);
const OBJECTS = new Set(['CHEST', 'CHEST_OPENED', 'BRAZIER', 'ALTAR']);
const LIGHTS = new Set(['BRAZIER', 'FIRE']);
const ACTIONS = new Set(['ATTACKED', 'ABILITY_USED', 'GUARDED', 'DEFAULT_GUARD_APPLIED', 'CHEST_OPENED', 'BLESSING_GAINED', 'REVIVED',
  'SHIELD_KINDLED', 'ATTACK_HINDERED', 'BEACON_LIT']);
/** Nombre de cada tramo de la resolución, para seguirla paso a paso. */
const CAPTION = { moves: 'Pasos', actions: 'Acciones', strikes: 'Golpes enemigos', enemies: 'Los enemigos avanzan', late: 'Final de la ronda' };
/** Sucesos de los caminos que se ven al final de la ronda (pasivas, ecos, apariciones). */
const LATE_PATH = new Set(['WARMTH_HEALED', 'ECHO_GAINED', 'EMBER_REBORN', 'RENEGADE_SPAWNED']);
/** Sucesos de los héroes que también se ven al final (fase de limpieza). */
const LATE_HERO = new Set(['DEBT_CHANGED', 'HOST_DRAINED', 'EMBERS_LOST', 'BETRAYAL_RECORDED', 'BETRAYAL_REVEALED', 'BELL_RUNG', 'SHOUTED']);
const DASH = [6, 5];
const NO_DASH = [];
const EMBER_COLORS = ['#ff9a3a', '#ffcf6a', '#ff6a2a'];
const SPIRIT_COLORS = ['#6fc8ff', '#c8ecff', '#3f9ae8'];
const BLOOD_COLORS = ['#ff4a3a', '#a8160e', '#ff8a6a'];
const BEACON_RADIUS = 2;

/** Radio de visión (Manhattan): el que manda el servidor (la sala Oscura, DIM y Ojo de búho lo bajan). Con Lámpara, toda la sala. */
export function visionRadius(me) {
  if (me?.blessings?.includes('LAMPARA')) return SIZE * 2;
  return me?.visionRadius ?? (me?.state === 'EMBER_SPIRIT' ? 4 : isBlind(me) ? 1 : 3);
}

const inBounds = (x, y) => x >= 0 && y >= 0 && x < SIZE && y < SIZE;
const tileKey = (x, y) => `${x},${y}`;
const isHeroId = (id) => Boolean(id) && !id.startsWith('enemy:');
const isSpirit = (d) => d?.state === 'EMBER_SPIRIT';
const isRenegade = (d) => d?.state === 'RENEGADE';
/**
 * Sin cuerpo propio en el tablero: el Renegado que todavía no apareció (su `position` es la vieja) y Polizón dentro de
 * su huésped, que se ve como el monstruo.
 */
const offBoard = (d) => (isRenegade(d) && Boolean(d.path?.awaitingSpawn)) || d?.hostEnemyId != null;
const manhattan = (a, b) => Math.abs(a.x - b.x) + Math.abs(a.y - b.y);
const secondId = (playerId) => `second:${playerId}`;
/** Segundo cuerpo de Hilván como una entidad más: comparte vida y mira al revés en este y oeste. */
const secondData = (d) => (d?.secondBody && !isSpirit(d)
  ? { ...d, position: d.secondBody, facing: d.facing ? mirrored(d.facing) : d.facing, secondary: true }
  : null);

function entityMap(snap) {
  const map = new Map();
  if (!snap) return map;
  const addHero = (d, isMe) => {
    map.set(d.playerId, { type: 'hero', d, isMe });
    const second = secondData(d);
    if (second) map.set(secondId(d.playerId), { type: 'hero', d: second, isMe, ownerId: d.playerId });
  };
  if (snap.me) addHero(offBoard(snap.me) ? { ...snap.me, position: null } : snap.me, true);
  for (const a of snap.allies ?? []) addHero(a, false);
  for (const e of snap.enemies ?? []) map.set(`enemy:${e.enemyId}`, { type: 'enemy', d: e, isMe: false });
  return map;
}

export class BoardRenderer {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.W = canvas.width;
    this.cell = this.W / SIZE;
    this.spriteScale = 3;

    this.state = { tiles: new Map(), me: null, allies: [], enemies: new Map(), pendingOrder: null, armedMode: null, mode: 'MOVE' };
    this.tiles = new Map(); // copia propia: match.js modifica su Map en el lugar
    this.lightTiles = [];
    this.layer = makeCanvas(1, 1);
    this.grayLayer = makeCanvas(1, 1);
    this.layerDirty = true;
    this.vision = new Float32Array(SIZE * SIZE);
    this.known = new Float32Array(SIZE * SIZE);
    this.fog = makeCanvas(SIZE * 4, SIZE * 4);
    this.fogCtx = this.fog.getContext('2d');
    this.fogImg = this.fogCtx.createImageData(SIZE * 4, SIZE * 4);
    this.fogDirty = true;
    this.light = makeCanvas(1, 1);
    this.lightCtx = this.light.getContext('2d');
    this.vignette = null;

    this.ents = new Map();
    this.order = [];
    this.timeline = new Timeline();
    this.effects = [];
    this.particles = new Particles(900);
    this.floaters = new Floaters(48);
    this.tileShown = new Map(); // casilla → tipo que se sigue mostrando hasta su momento en la resolución
    this.teleKeys = [];
    this.teleSet = new Set();
    this.teleAlpha = 1;
    this.preview = [];
    this.previewFaint = [];
    this.previewTmp = [];
    this.trauma = 0;
    this.flashA = 0;
    this.flashColor = '#ffffff';
    this.fade = 0;
    this.banner = null;
    this.beacons = []; // Faros encendidos en la última resolución: zona iluminada hasta la siguiente
    // Capas de los héroes que se dibujan: siguen al estado lógico solo al terminar la animación de la resolución.
    this.overlay = { hidden: [], echoes: [], ground: [], noise: [] };
    this.shout = false; // Grito de este bando: se ve la sala entera hasta la próxima resolución
    this.shiftAnim = null; // corrimiento en curso { axis, index, sign, p }
    this.pending = null;
    this.pendingFloor = null;
    this.hoverDir = null;
    this.orderHidden = false;
    this.now = performance.now();
    this.t = this.now / 1000;
    this.emberClock = 0;
    this.settings = getSettings();
    this.screenOn = true;
    this.disposed = false;
    this.raf = 0;
    this.last = this.now;

    this.frame = this.frame.bind(this);
    this.offs = [
      on('round:resolved', (d) => { this.pending = d; this.orderHidden = true; }),
      on('round:opened', () => { this.orderHidden = false; }),
      on('floor:changed', (d) => { this.pendingFloor = d.floor; this.nextRoom = d.room ?? null; }),
      on('settings:changed', (s) => { this.settings = s; }),
      on('ui:screen', ({ name }) => { this.screenOn = name === 'match'; this.updateLoop(); }),
      on('match:stop', () => this.dispose()),
      on('signal:received', (s) => signalFx(this, s)),
    ];
    // Clic en el tablero: casilla para la próxima señal rápida.
    this.onCanvasClick = (e) => {
      const box = canvas.getBoundingClientRect();
      if (!box.width) return;
      const x = Math.floor(((e.clientX - box.left) / box.width) * SIZE);
      const y = Math.floor(((e.clientY - box.top) / box.height) * SIZE);
      if (inBounds(x, y)) emit('board:tile', { x, y });
    };
    canvas.addEventListener('click', this.onCanvasClick);
    this.onVisibility = () => this.updateLoop();
    this.onHover = (e) => {
      const btn = e.target?.closest?.('.dir[data-dir]');
      if (btn) this.hoverDir = btn.dataset.dir;
    };
    this.onLeave = (e) => {
      if (e.target?.closest?.('.dir[data-dir]')) this.hoverDir = null;
    };
    document.addEventListener('visibilitychange', this.onVisibility);
    document.addEventListener('pointerover', this.onHover);
    document.addEventListener('pointerout', this.onLeave);
    document.addEventListener('focusin', this.onHover);
    document.addEventListener('focusout', this.onLeave);
    this.onResize = () => this.resize();
    window.addEventListener('resize', this.onResize);
    this.observer = typeof ResizeObserver === 'function' ? new ResizeObserver(this.onResize) : null;
    this.observer?.observe(canvas);
    this.resize();
    this.updateLoop();
  }

  dispose() {
    if (this.disposed) return;
    this.disposed = true;
    this.updateLoop();
    this.offs.forEach((off) => off());
    this.canvas.removeEventListener('click', this.onCanvasClick);
    document.removeEventListener('visibilitychange', this.onVisibility);
    document.removeEventListener('pointerover', this.onHover);
    document.removeEventListener('pointerout', this.onLeave);
    document.removeEventListener('focusin', this.onHover);
    document.removeEventListener('focusout', this.onLeave);
    window.removeEventListener('resize', this.onResize);
    this.observer?.disconnect();
  }

  // ───────────── tamaño y bucle ─────────────

  /** El canvas se dimensiona en píxeles de dispositivo (múltiplo de 9 para que las casillas sean exactas). */
  resize() {
    const css = this.canvas.getBoundingClientRect().width;
    if (!css) return;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    const W = Math.max(SIZE * 16, Math.round((css * dpr) / SIZE) * SIZE);
    if (W !== this.canvas.width || W !== this.W) {
      this.canvas.width = W;
      this.canvas.height = W;
      this.W = W;
      this.cell = W / SIZE;
      this.light.width = W;
      this.light.height = W;
      this.layerDirty = true;
      this.vignette = null;
    }
    // Escala entera para que los píxeles de los sprites queden nítidos.
    const ideal = (this.cell * 0.9) / 18;
    this.spriteScale = ideal >= 2 ? Math.floor(ideal) : ideal;
    // Cambiar el tamaño borra el canvas: se repinta ya para no mostrar un cuadro negro.
    if (!this.disposed && this.vignette === null) this.render();
  }

  updateLoop() {
    const run = !this.disposed && this.screenOn && !document.hidden;
    if (run && !this.raf) {
      this.last = performance.now();
      this.raf = requestAnimationFrame(this.frame);
    } else if (!run && this.raf) {
      cancelAnimationFrame(this.raf);
      this.raf = 0;
    }
  }

  frame(now) {
    this.raf = requestAnimationFrame(this.frame);
    const dt = Math.min(0.05, Math.max(0, (now - this.last) / 1000));
    this.last = now;
    this.now = now;
    this.t = now / 1000;
    if (this.pending) this.consumePending([]);
    this.timeline.update(now);
    this.updatePreview();
    this.update(dt);
    this.render();
  }

  get rm() { return Boolean(this.settings.reducedMotion); }

  // ───────────── estado lógico → visual ─────────────

  /** Lo llama match.js en cada render con el estado lógico; el dibujo ocurre en el bucle. */
  draw(state) {
    this.state = state;
    const changes = this.syncTiles(state.tiles);
    if (this.pending) this.consumePending(changes);
    this.syncEntities();
    if (!this.timeline.running) this.syncTelegraphs(false);
    this.updatePreview();
    if (!this.raf && !this.disposed) this.render(); // pestaña oculta: al menos un cuadro fiel
  }

  syncTiles(tiles) {
    const changes = [];
    for (const [k, kind] of tiles) {
      const prev = this.tiles.get(k);
      if (prev !== kind) changes.push({ key: k, from: prev, to: kind });
    }
    if (changes.length || tiles.size !== this.tiles.size) {
      this.tiles = new Map(tiles);
      this.layerDirty = true;
      this.fogDirty = true;
      this.lightTiles = [];
      for (const [k, kind] of this.tiles) {
        if (!LIGHTS.has(kind)) continue;
        const [x, y] = k.split(',').map(Number);
        this.lightTiles.push({ x, y, key: k });
      }
    }
    return changes;
  }

  newEnt(id, type, d, isMe) {
    const pos = d.position ?? { x: 0, y: 0 };
    return {
      id, type, d, isMe, x: pos.x, y: pos.y, tx: pos.x, ty: pos.y, hop: 0, ox: 0, oy: 0,
      alpha: 0, alphaTarget: 1, flash: 0, jolt: 0, frozen: false, hp: d.hp, maxHp: d.maxHp, trail: d.hp,
      facing: d.facing ?? 'S', seed: Math.random() * 6.28, locked: false, leaving: false, dead: false,
      downT: d.state === 'DOWN' ? 1 : 0, downTarget: d.state === 'DOWN' ? 1 : 0,
    };
  }

  /** Héroe de un jugador (para sus Ecos). */
  heroOf(playerId) {
    const { me, allies } = this.state;
    return me?.playerId === playerId ? me.hero : allies?.find((a) => a.playerId === playerId)?.hero;
  }

  /** Lo que el jugador tiene en el tablero: su héroe o, si va de polizón, su huésped. */
  meEnt() {
    const me = this.state.me;
    if (!me) return null;
    return me.hostEnemyId != null ? this.ents.get(`enemy:${me.hostEnemyId}`) ?? null : this.ents.get(me.playerId) ?? null;
  }

  /** Casilla desde la que actúa el jugador. */
  origin() {
    const me = this.state.me;
    return (me?.hostEnemyId != null ? this.state.enemies?.get(me.hostEnemyId)?.position : null) ?? me?.position ?? null;
  }

  /** Paso marcado o ya enviado con la orden. */
  stepInPlay() {
    const s = this.state;
    return s.pendingOrder ? s.pendingOrder.step ?? null : s.plannedStep ?? null;
  }

  /**
   * Dónde quedaría con ese paso, según lo que se ve: un cuerpo que no puede entrar se queda (y la acción sale de ahí). No
   * es una promesa: otro jugador puede ir a la misma casilla, y entonces rebotan.
   */
  landing(direction) {
    const me = this.state.me;
    const start = this.origin();
    if (!start) return null;
    const v = DIRS[direction];
    const occupied = (x, y) => {
      const { allies, enemies } = this.state;
      for (const e of enemies?.values?.() ?? []) if (e.position?.x === x && e.position?.y === y && e.enemyId !== me?.hostEnemyId) return true;
      return (allies ?? []).some((a) => a.state !== 'EMBER_SPIRIT' && a.position?.x === x && a.position?.y === y);
    };
    if (isSpirit(me)) {
      let { x, y } = start;
      for (let i = 0; i < 2 && inBounds(x + v[0], y + v[1]); i++) { x += v[0]; y += v[1]; }
      return { pos: { x, y }, second: null, bounced: false };
    }
    const walk = (from, d) => {
      const w = DIRS[d];
      const to = { x: from.x + w[0], y: from.y + w[1] };
      const blocked = !inBounds(to.x, to.y) || STOPS_STEP.has(this.tiles.get(tileKey(to.x, to.y))) || occupied(to.x, to.y);
      return { to, pos: blocked ? from : to, blocked };
    };
    const main = walk(start, direction);
    const other = me?.secondBody && me.hostEnemyId == null ? walk(me.secondBody, mirrored(direction)) : null;
    return { pos: main.pos, target: main.to, second: other?.pos ?? null, secondTarget: other?.to ?? null, bounced: main.blocked, secondBounced: Boolean(other?.blocked) };
  }

  upsert(id, type, d, isMe) {
    let v = this.ents.get(id);
    if (!v) {
      v = this.newEnt(id, type, d, isMe);
      this.ents.set(id, v);
    }
    if (d.secondary) v.ownerId = d.playerId;
    v.d = d;
    v.isMe = isMe;
    v.seen = true;
    if (v.locked) return;
    v.leaving = false;
    v.dead = false;
    v.tx = d.position.x;
    v.ty = d.position.y;
    v.hp = d.hp;
    v.maxHp = d.maxHp;
    v.facing = d.facing ?? v.facing;
    v.downTarget = d.state === 'DOWN' || d.state === 'EXTINCT' ? 1 : 0;
    v.alphaTarget = d.state === 'EXTINCT' ? 0.35 : 1;
  }

  syncEntities() {
    const { me, allies, enemies } = this.state;
    for (const v of this.ents.values()) v.seen = false;
    const addHero = (d, isMe) => {
      this.upsert(d.playerId, 'hero', d, isMe);
      const second = secondData(d);
      if (second) this.upsert(secondId(d.playerId), 'hero', second, isMe);
    };
    if (me?.position && !offBoard(me)) addHero(me, true);
    for (const a of allies ?? []) if (a.position) addHero(a, false);
    for (const e of enemies?.values() ?? []) if (e.position) this.upsert(`enemy:${e.enemyId}`, 'enemy', e, false);
    for (const v of this.ents.values()) {
      if (!v.seen && !v.locked) { v.leaving = true; v.alphaTarget = 0; }
    }
  }

  syncTelegraphs(fadeIn) {
    const keys = [];
    const set = new Set();
    for (const e of this.state.enemies?.values() ?? []) {
      for (const p of e.telegraph ?? []) {
        const k = tileKey(p.x, p.y);
        if (!set.has(k) && inBounds(p.x, p.y)) { set.add(k); keys.push(p); }
      }
    }
    const changed = keys.length !== this.teleKeys.length || keys.some((p) => !this.teleSet.has(tileKey(p.x, p.y)));
    this.teleKeys = keys;
    this.teleSet = set;
    if (fadeIn && changed) this.teleAlpha = 0;
    // Las capas de los héroes siguen al estado lógico en el mismo momento que los anuncios.
    const s = this.state;
    this.overlay = { hidden: (s.hiddenTelegraphs ?? []).filter((p) => !set.has(tileKey(p.x, p.y))), echoes: s.echoes ?? [], ground: s.groundEmbers ?? [], noise: s.noiseTiles ?? [] };
  }

  // ───────────── vista previa de la orden ─────────────

  /** Casillas que afectaría una orden del héroe propio (aproximación visual de las reglas). */
  affected(type, dir, out) {
    out.length = 0;
    const me = this.state.me;
    const origin = this.origin();
    const hosted = me?.hostEnemyId != null;
    if (!origin || !(me.state === 'ALIVE' || isSpirit(me) || (isRenegade(me) && !(me.path?.awaitingSpawn)))) return out;
    // Con un paso en juego, la acción sale de donde quedaría.
    const step = type === 'MOVE' ? null : this.stepInPlay();
    const landed = step ? this.landing(step) : null;
    const { x, y } = landed?.pos ?? origin;
    if (isSpirit(me)) return this.spiritAffected(type, dir, x, y, out);
    const blocked = (px, py) => !inBounds(px, py) || STOPS_SHOTS.has(this.tiles.get(tileKey(px, py)));
    const ray = (from, d, len) => {
      const [dx, dy] = DIRS[d];
      for (let i = 1; i <= len; i++) {
        const px = from.x + dx * i;
        const py = from.y + dy * i;
        if (blocked(px, py)) break;
        out.push({ x: px, y: py });
      }
    };
    const second = isSpirit(me) ? null : landed?.second ?? me.secondBody;
    if (type === 'GUARD') {
      if (!hosted) out.push({ x, y });
      if (second) out.push(second);
      return out;
    }
    if (type === 'ABILITY' && me.hero === 'ANTEAYER') {
      if (me.rewindTile) out.push(me.rewindTile);
      return out;
    }
    if (type === 'ABILITY' && me.hero === 'HILVAN') {
      out.push(...tensarCells(me.position, second, this.tiles));
      return out;
    }
    const v = DIRS[dir];
    if (!v) return out;
    const from0 = { x, y };
    const stepFrom = (from, d) => {
      const [dx, dy] = DIRS[d];
      if (inBounds(from.x + dx, from.y + dy)) out.push({ x: from.x + dx, y: from.y + dy });
    };
    if (hosted) {
      // Dentro del huésped: mover lo mueve, atacar golpea con su alcance, la habilidad sale. Guardar e interactuar no hacen nada.
      if (type === 'MOVE' || type === 'ABILITY') stepFrom(from0, dir);
      else if (type === 'ATTACK') ray(from0, dir, this.state.enemies?.get(me.hostEnemyId)?.kind === 'BALLESTERO' ? 8 : 1);
      return out;
    }
    if (type === 'SABOTAGE') {
      // Busca al primer aliado en línea desde el cuerpo principal y, si no hay, desde el segundo.
      ray(from0, dir, 2);
      if (second) ray(second, mirrored(dir), 2);
      return out;
    }
    if (type === 'PUSH') {
      ray(from0, dir, 2);
      if (second) ray(second, mirrored(dir), 2);
      return out;
    }
    if (type === 'MOVE' || type === 'INTERACT') {
      stepFrom(from0, dir);
      if (second) stepFrom(second, mirrored(dir));
    } else if (type === 'ATTACK') {
      const range = heroInfo(me.hero).range ?? 1;
      ray(from0, dir, range);
      if (second) ray(second, mirrored(dir), range);
    } else if (type === 'ABILITY') {
      if (me.hero === 'ERRATA') correctedTiles(from0, dir, this.state.enemies, this.state.hiddenTelegraphs, this.tiles, out);
      else if (me.hero === 'POLIZON') stepFrom(from0, dir);
      else if (me.hero === 'USURERA') ray(from0, dir, 2);
      else if (me.hero === 'AVARO') ray(from0, dir, 3);
      else if (me.hero === 'MAMPOSTERO') {
        const s = shiftFromOrder(origin, dir);
        for (let i = 0; i < SIZE; i++) out.push(s.axis === 'row' ? { x: i, y: s.index } : { x: s.index, y: i });
      } else out.push({ x, y }); // Grito y Repique no apuntan a ninguna parte.
    }
    return out;
  }

  /** Brasa errante: nada la detiene (Deriva, Avivar y Estorbo atraviesan muros); el Faro ilumina un radio de 2. */
  spiritAffected(type, dir, x, y, out) {
    if (type === 'GUARD') {
      for (let dy = -BEACON_RADIUS; dy <= BEACON_RADIUS; dy++) {
        for (let dx = -BEACON_RADIUS; dx <= BEACON_RADIUS; dx++) {
          if (Math.abs(dx) + Math.abs(dy) <= BEACON_RADIUS && inBounds(x + dx, y + dy)) out.push({ x: x + dx, y: y + dy });
        }
      }
      return out;
    }
    const v = DIRS[dir];
    if (!v) return out;
    const reach = type === 'INTERACT' ? 1 : 2;
    for (let i = 1; i <= reach; i++) {
      if (inBounds(x + v[0] * i, y + v[1] * i)) out.push({ x: x + v[0] * i, y: y + v[1] * i });
    }
    return out;
  }

  updatePreview() {
    const s = this.state;
    this.preview.length = 0;
    this.previewFaint.length = 0;
    this.previewMode = null;
    this.previewStep = s.pendingOrder || !s.plannedStep ? null : this.landing(s.plannedStep);
    if (s.pendingOrder || !s.me?.position || this.timeline.running) return;
    const mode = s.armedMode ?? s.mode ?? 'MOVE';
    this.previewMode = mode;
    if (mode === 'ABILITY' && (s.me.hero === 'ANTEAYER' || s.me.hero === 'HILVAN') && !isSpirit(s.me) && s.me.hostEnemyId == null) {
      // Volver y Tensar no dependen de la dirección: se ven en cuanto se elige la habilidad.
      this.affected(mode, null, this.preview);
    } else if (this.hoverDir) {
      this.affected(mode, this.hoverDir, this.preview);
    } else if (s.armedMode) {
      const tmp = this.previewTmp;
      for (const d of DIR_NAMES) {
        this.affected(mode, d, tmp);
        for (const p of tmp) if (!this.previewFaint.some((q) => q.x === p.x && q.y === p.y)) this.previewFaint.push(p);
      }
    }
  }

  // ───────────── resolución de una ronda ─────────────

  consumePending(tileChanges) {
    const detail = this.pending;
    this.pending = null;
    this.timeline.finish();
    // Los Faros duran hasta la resolución siguiente; el Grito, también.
    this.beacons = [];
    const me = this.state.me;
    const sideOf = (d) => (isRenegade(d) ? 'dungeon' : 'group');
    const shouter = (detail.events ?? []).find((ev) => ev.type === 'SHOUTED')?.actorId;
    const shouterData = shouter === me?.playerId ? me : detail.after?.allies?.find((a) => a.playerId === shouter);
    this.shout = Boolean(shouterData) && sideOf(shouterData) === sideOf(detail.after?.me ?? me);
    if (this.pendingFloor !== null) {
      const floor = this.pendingFloor;
      this.pendingFloor = null;
      this.floorTransition(floor, detail);
      return;
    }
    this.playResolution(detail, tileChanges);
  }

  floorTransition(floor, detail) {
    this.fade = 1;
    this.ents.clear();
    this.effects.length = 0;
    this.particles.clear();
    this.floaters.clear();
    this.tileShown.clear();
    this.known.fill(0);
    this.vision.fill(0);
    this.teleAlpha = 0;
    let sub = floor >= 5 ? 'La sala del Celador' : this.nextRoom?.name ?? 'Seguid bajando';
    this.nextRoom = null;
    // Al bajar renacen las Brasas (en la escalera) y aparecen los Renegados (lejos del grupo).
    const events = (detail?.events ?? []).filter((ev) => ev.type === 'EMBER_REBORN' || ev.type === 'RENEGADE_SPAWNED');
    const mine = events.find((ev) => ev.actorId === this.state.me?.playerId);
    if (mine?.type === 'EMBER_REBORN') sub = `Renaces en la escalera con ${mine.amount ?? '?'} de vida`;
    else if (mine?.type === 'RENEGADE_SPAWNED') sub = 'Vuelves como Traidor, lejos de los héroes';
    this.banner = { text: `Piso ${floor}`, sub, start: this.now, dur: 2600, color: mine?.type === 'RENEGADE_SPAWNED' ? '#ff8a7a' : undefined };
    this.syncEntities();
    for (const v of this.ents.values()) { v.x = v.tx; v.y = v.ty; }
    if (!events.length) return;
    const tl = this.timeline;
    events.forEach((ev, i) => {
      const v = this.ents.get(ev.actorId);
      if (v) { v.alpha = 0; v.alphaTarget = 0; v.locked = true; } // aparece con su efecto
      tl.at(900 + i * 450, () => this.applyEvent(ev, null, true));
    });
    tl.at(900 + events.length * 450 + 300, () => {
      for (const v of this.ents.values()) v.locked = false;
      this.syncEntities();
      this.syncTelegraphs(true);
    });
    tl.start(this.now);
  }

  playResolution(detail, tileChanges) {
    const tl = this.timeline;
    const rm = this.rm;
    const S = rm ? 0.5 : 1;
    const events = detail.events ?? [];
    const B = entityMap(detail.before);
    const A = entityMap(detail.after);
    // Corrimiento del Mampostero: ocurre antes de los movimientos, y lo que se mueve después parte de la casilla corrida.
    const shift = shiftFromEvent(events.find((ev) => ev.type === 'ROOM_SHIFTED'));
    const startOf = (d) => (shift && d?.position && !isSpirit(d) ? shiftedPos(shift, d.position) : d?.position);

    // 1) Estado visual de partida: el "antes".
    const ids = new Set([...B.keys(), ...A.keys()]);
    for (const id of ids) {
      const b = B.get(id);
      const a = A.get(id);
      const src = a ?? b;
      let v = this.ents.get(id);
      if (!v) {
        const pos = b?.d.position ?? a?.d.position;
        if (!pos) continue;
        v = this.newEnt(id, src.type, { ...src.d, position: pos }, src.isMe);
        v.alpha = b?.d.position ? 1 : 0;
        if (src.ownerId) v.ownerId = src.ownerId;
        this.ents.set(id, v);
      }
      v.locked = true;
      v.isMe = src.isMe;
      v.frozen = false;
      v.d = a?.d.position ? a.d : (b?.d ?? v.d);
      // El extinto que eligió Amistad sigue tendido hasta que se revela su camino (PATH_CHOSEN).
      if (b?.d.state === 'EXTINCT' && b.d.position && isSpirit(a?.d)) { v.d = b.d; v.revealTo = a.d; }
      if (b?.d.position) {
        v.x = b.d.position.x; v.y = b.d.position.y;
        v.hp = b.d.hp; v.maxHp = b.d.maxHp; v.trail = Math.max(v.trail, v.hp);
        v.facing = b.d.facing ?? v.facing;
        v.leaving = false; v.dead = false;
        v.alphaTarget = b.d.state === 'EXTINCT' ? 0.35 : 1;
        if (v.alpha < 0.5) v.alpha = 1;
      } else if (a?.d.position) {
        v.x = a.d.position.x; v.y = a.d.position.y;
        v.hp = a.d.hp; v.maxHp = a.d.maxHp; v.trail = v.hp;
        v.alpha = 0; v.alphaTarget = 0;
      }
      v.ox = 0; v.oy = 0; v.hop = 0;
    }

    // Casillas que cambian: se ve lo de antes hasta su momento (cofre que se abre, fuego que prende…).
    // Lo de la línea corrida no cuenta: esas casillas ya se ven deslizarse.
    const onLine = (k) => { const [x, y] = k.split(',').map(Number); return onShiftLine(shift, x, y); };
    for (const c of tileChanges) if (c.from !== undefined && !onLine(c.key)) this.tileShown.set(c.key, c.from);

    // Ataques anunciados de la ronda que se resuelve: siguen a la vista hasta que golpean. Un enemigo
    // estorbado por una Brasa o sobornado conserva el anuncio pero no golpea; uno corregido golpea donde cayó el anuncio.
    const hinderedIds = new Set(events.filter((ev) => (ev.type === 'ATTACK_HINDERED' || ev.type === 'ENEMY_BRIBED') && ev.targetId).map((ev) => ev.targetId));
    const corrected = new Map(events.filter((ev) => ev.type === 'ATTACK_CORRECTED' && ev.targetId).map((ev) => [ev.targetId, ev.tiles ?? []]));
    const strikes = [];
    const oldTele = [];
    const oldSet = new Set();
    for (const [id, b] of B) {
      if (b.type !== 'enemy' || !b.d.telegraph?.length) continue;
      if (!hinderedIds.has(id)) strikes.push({ id, d: corrected.has(id) ? { ...b.d, telegraph: corrected.get(id) } : b.d });
      for (const p of b.d.telegraph) {
        const k = tileKey(p.x, p.y);
        if (!oldSet.has(k)) { oldSet.add(k); oldTele.push(p); }
      }
    }
    this.teleKeys = oldTele;
    this.teleSet = oldSet;
    this.teleAlpha = 1;

    // 2) Clasificar los sucesos por fase.
    const groups = new Map();
    const envEvents = [];
    const lateEvents = [];
    const pathEvents = [];
    const bounces = [];
    const pushes = [];
    const loose = []; // sin autor en la fase de acciones: sabotajes
    const addTo = (id, ev) => { if (!groups.has(id)) groups.set(id, []); groups.get(id).push(ev); };
    const heroKind = (id) => (A.get(id) ?? B.get(id))?.d.hero;
    const warmed = new Set(events.filter((ev) => ev.type === 'WARMTH_HEALED').map((ev) => ev.targetId));
    // Quien renace o aparece como Renegado se hace visible con su propio efecto, al final.
    const appearsLate = new Set(events.filter((ev) => ev.type === 'EMBER_REBORN' || ev.type === 'RENEGADE_SPAWNED').map((ev) => ev.actorId));
    for (const ev of events) {
      switch (ev.type) {
        case 'MOVED': case 'ROOM_SHIFTED': case 'ATTACK_CORRECTED': break;
        case 'BOUNCED': bounces.push(ev); break;
        case 'PUSHED': pushes.push(ev); break;
        case 'DAMAGED': case 'BLOCKED': case 'ENEMY_KILLED': case 'RENEGADE_DEFEATED':
          if (isHeroId(ev.actorId)) addTo(ev.actorId, ev);
          else if (!ev.actorId && ev.phase === 'ACTIONS') loose.push(ev);
          else envEvents.push(ev);
          break;
        case 'EMBERS_GAINED':
          if (groups.has(ev.actorId)) addTo(ev.actorId, ev); else lateEvents.push(ev);
          break;
        case 'HEALED': {
          // La Calidez de una Brasa cura al final de la ronda, junto a su propio suceso.
          if (warmed.has(ev.targetId)) { lateEvents.push(ev); break; }
          const healer = [...groups.keys()].find((id) => heroKind(id) === 'USURERA' && groups.get(id).some((e) => e.type === 'ABILITY_USED'));
          if (healer) addTo(healer, ev); else envEvents.push(ev);
          break;
        }
        case 'ENEMY_BRIBED': case 'HOST_BOARDED': case 'WARDEN_TURNED':
          // La causa es una habilidad: va con la acción de quien la usó.
          if (isHeroId(ev.actorId)) addTo(ev.actorId, ev); else envEvents.push(ev);
          break;
        case 'BOSS_PHASE_CHANGED': case 'EMBERS_STOLEN': case 'FLOOR_ENTERED':
          lateEvents.push(ev);
          break;
        case 'PATH_CHOSEN':
          pathEvents.push(ev);
          break;
        default:
          if (LATE_PATH.has(ev.type) || LATE_HERO.has(ev.type)) lateEvents.push(ev);
          else if (ACTIONS.has(ev.type) && isHeroId(ev.actorId)) addTo(ev.actorId, ev);
          else envEvents.push(ev);
      }
    }

    // 2b) Al inicio de la resolución se revelan los caminos elegidos.
    pathEvents.forEach((ev, i) => tl.at(i * 260 * S, () => this.applyEvent(ev)));
    const tPaths = pathEvents.length ? (520 + (pathEvents.length - 1) * 260) * S : 0;

    // 2c) Corrimiento: la línea entera se desliza (mapa y criaturas), y quien cruza el borde entra por el otro lado.
    if (shift) this.playShift(shift, ids, B, A, startOf, tPaths, S);
    const t0 = tPaths + (shift ? 560 * S : 0);

    // Anuncios desviados por Errata: se ven saltar antes de que nadie se mueva.
    const corrections = events.filter((ev) => ev.type === 'ATTACK_CORRECTED');
    if (corrections.length) {
      tl.at(tPaths, () => {
        const kept = [...corrected.keys()].flatMap((id) => B.get(id)?.d.telegraph ?? []);
        const gone = new Set(kept.map((p) => tileKey(p.x, p.y)));
        const moved2 = corrections.flatMap((ev) => ev.tiles ?? []);
        this.teleKeys = [...this.teleKeys.filter((p) => !gone.has(tileKey(p.x, p.y))), ...moved2];
        this.teleSet = new Set(this.teleKeys.map((p) => tileKey(p.x, p.y)));
        corrections.forEach((ev) => this.applyEvent(ev));
      });
    }

    // Un empujón se ve en su momento: quien lo recibe no se desliza antes de tiempo.
    const pushedTo = new Map();
    for (const ev of pushes) pushedTo.set(ev.targetId, ev.position);

    // 3) Movimientos de héroes (la Brasa deriva atravesando todo; Anteayer salta a su pasado).
    let moved = false;
    for (const id of ids) {
      const b = B.get(id);
      const a = A.get(id);
      const v = this.ents.get(id);
      if (!v || (b ?? a).type !== 'hero') continue;
      const ownerId = v.ownerId ?? id;
      const rewinds = heroKind(ownerId) === 'ANTEAYER' && groups.get(ownerId)?.some((e) => e.type === 'ABILITY_USED');
      const from = startOf(b?.d);
      if (pushedTo.has(id)) {
        // Se mueve con el empujón, más abajo.
      } else if (from && a?.d.position && (from.x !== a.d.position.x || from.y !== a.d.position.y)) {
        moved = true;
        if (rewinds) this.rewindStep(v, from, a.d.position, t0, S);
        else if (isSpirit(a.d) && isSpirit(b.d)) this.driftTween(v, b.d.position, a.d.position, t0, 420 * S);
        else this.moveTween(v, from, a.d.position, t0, 340 * S, rm ? 0 : 0.28);
      } else if (b?.d.position && !a?.d.position) {
        // Dejó de verse, abordó a un monstruo o se fue como Renegado.
        tl.at(t0, () => { v.alphaTarget = 0; v.leaving = true; });
        moved = true;
      } else if (!b?.d.position && a?.d.position && !appearsLate.has(ownerId)) {
        tl.at(t0 + 120 * S, () => { v.alphaTarget = 1; });
      }
    }
    for (const ev of bounces) {
      const bodyId = ev.detail === 'SECOND_BODY' ? secondId(ev.actorId) : ev.actorId;
      const v = this.ents.get(bodyId);
      if (!v) continue;
      moved = true;
      const after = A.get(bodyId)?.d;
      let dir = DIRS[after?.facing] ?? [0, 0];
      if (ev.position && (ev.position.x !== v.x || ev.position.y !== v.y)) dir = [Math.sign(ev.position.x - v.x), Math.sign(ev.position.y - v.y)];
      if (after?.facing) tl.at(t0, () => { v.facing = after.facing; });
      tl.tween(t0, 300 * S, (p) => {
        const k = (p < 0.4 ? ease.outCubic(p / 0.4) : 1 - ease.inOutQuad((p - 0.4) / 0.6)) * 0.3;
        v.ox = dir[0] * k; v.oy = dir[1] * k;
      }, () => { v.ox = 0; v.oy = 0; });
      tl.at(t0 + 120 * S, () => {
        this.particles.burst(v.x + 0.5 + dir[0] * 0.45, v.y + 0.5 + dir[1] * 0.45, 8, { speed: 1.6, life: 0.4, size: 0.05, colors: ['#d8ccb8', '#8a8070'], drag: 4 });
        this.floaters.add('¡bonk!', v.x + 0.5, v.y + 0.05, { color: '#d8ccb8', size: 0.2, life: 0.8 });
        this.shake(0.12);
      });
    }
    if (moved) this.showCaption(CAPTION.moves, t0);
    const tActions = t0 + (moved ? 380 * S : 0);
    if (groups.size || pushes.length || loose.length) this.showCaption(CAPTION.actions, tActions);

    // 4) Acciones de héroes (y sus consecuencias, al momento del impacto).
    let cursor = tActions;
    let endB = tActions;
    for (const [actorId, list] of groups) {
      const end = this.playHeroGroup(actorId, list, cursor, S, A, B);
      endB = Math.max(endB, end);
      cursor += 160 * S;
    }

    // Empujones y sabotajes, en la misma fase.
    pushes.forEach((ev, i) => {
      const t = cursor + i * 260 * S;
      const v = this.ents.get(ev.targetId);
      const actor = this.ents.get(ev.actorId);
      if (actor) {
        const dir = DIRS[(A.get(ev.actorId) ?? B.get(ev.actorId))?.d.facing];
        if (dir) this.lunge(actor, dir, t, S);
      }
      tl.at(t + 90 * S, () => this.applyEvent(ev));
      const from = startOf(B.get(ev.targetId)?.d);
      if (v && from && ev.position) this.moveTween(v, from, ev.position, t + 90 * S, 240 * S, 0);
      endB = Math.max(endB, t + 340 * S);
    });
    loose.forEach((ev, i) => {
      const t = cursor + pushes.length * 260 * S + i * 200 * S;
      tl.at(t, () => this.applyEvent(ev));
      endB = Math.max(endB, t + 260 * S);
    });

    // 5) Enemigos: golpean lo anunciado, luego se mueven.
    const tStrike = endB + (groups.size ? 120 * S : 0);
    if (strikes.length) this.showCaption(CAPTION.strikes, tStrike);
    tl.at(tStrike, () => { this.teleKeys = []; this.teleSet = new Set(); this.overlay.hidden = []; this.overlay.noise = []; });
    strikes.forEach((s, i) => this.enemyStrike(s, tStrike + Math.min(i, 5) * 50 * S, S));
    const tHit = tStrike + (strikes.length ? 170 * S : 0);
    envEvents.forEach((ev, i) => tl.at(tHit + i * 110 * S, () => this.applyEvent(ev)));
    for (const c of tileChanges) if (c.from !== undefined) tl.at(tHit + envEvents.length * 110 * S, () => this.tileShown.delete(c.key));
    const tMove = tHit + (envEvents.length ? envEvents.length * 110 * S + 140 * S : strikes.length ? 200 * S : 0);
    let enemyMoved = false;
    for (const id of ids) {
      const b = B.get(id);
      const a = A.get(id);
      const v = this.ents.get(id);
      if (!v || (b ?? a).type !== 'enemy') continue;
      const from = pushedTo.get(id) ?? startOf(b?.d);
      if (from && a?.d.position && (from.x !== a.d.position.x || from.y !== a.d.position.y)) {
        enemyMoved = true;
        const kind = a.d.kind;
        const hop = rm ? 0 : kind === 'SALTASAPO' ? 0.6 : kind === 'POLILLA' ? 0 : 0.14;
        this.moveTween(v, from, a.d.position, tMove, (kind === 'SALTASAPO' ? 440 : 340) * S, hop);
        tl.at(tMove, () => { v.facing = a.d.facing ?? v.facing; });
      } else if (b?.d.position && !a?.d.position) {
        tl.at(tMove, () => { if (!v.dead) { v.alphaTarget = 0; v.leaving = true; } });
      } else if (!b?.d.position && a?.d.position) {
        tl.at(tMove + 100 * S, () => { if (v.alphaTarget === 0) v.alphaTarget = 1; });
      } else if (a?.d) {
        tl.at(tMove, () => { v.facing = a.d.facing ?? v.facing; });
      }
    }
    if (enemyMoved) this.showCaption(CAPTION.enemies, tMove);
    const tLate = tMove + (enemyMoved ? 380 * S : 60 * S);
    if (lateEvents.length) this.showCaption(CAPTION.late, tLate);
    lateEvents.forEach((ev, i) => tl.at(tLate + i * 220 * S, () => this.applyEvent(ev)));
    const tEnd = tLate + (lateEvents.length ? lateEvents.length * 220 * S + 200 * S : 0);

    // 6) Fin: todo vuelve a seguir al estado lógico y se anuncian los ataques de la ronda siguiente.
    tl.at(tEnd, () => {
      for (const v of this.ents.values()) { v.locked = false; v.ox = 0; v.oy = 0; v.hop = 0; }
      this.tileShown.clear();
      this.syncEntities();
      for (const v of this.ents.values()) { if (!v.leaving) { v.x = v.tx; v.y = v.ty; } }
      this.syncTelegraphs(true);
      this.caption = null;
    });
    tl.start(this.now);
  }

  /** Rótulo del tramo de la resolución que se está viendo. */
  showCaption(text, t) {
    this.timeline.at(t, () => { this.caption = { text, start: this.now }; });
  }

  drawCaption(g, cell) {
    const c = this.caption;
    if (!c) return;
    const age = (this.now - c.start) / 1000;
    const alpha = Math.min(1, age / 0.12);
    const size = Math.round(cell * 0.26);
    g.font = `700 ${size}px Georgia, serif`;
    const w = g.measureText(c.text).width + size * 1.4;
    const x = (this.W - w) / 2;
    const y = cell * 0.12;
    g.globalAlpha = alpha * 0.82;
    g.fillStyle = '#120a06';
    g.fillRect(x, y, w, size * 1.6);
    g.globalAlpha = alpha;
    g.strokeStyle = '#c98a3e';
    g.lineWidth = 1;
    g.strokeRect(x + 0.5, y + 0.5, w - 1, size * 1.6 - 1);
    g.fillStyle = '#ffe2a8';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(c.text, this.W / 2, y + size * 0.82);
    g.globalAlpha = 1;
  }

  /** Sala inundada: las filas del norte, en azul, con oleaje. Lo que hay encima se sigue viendo. */
  drawFlood(g, cell) {
    const rows = this.state.floodedRows ?? 0;
    if (!rows) return;
    const wave = this.rm ? 0 : Math.sin(this.t * 1.6) * 0.05;
    g.fillStyle = '#2f7fd8';
    g.globalAlpha = 0.34 + wave;
    g.fillRect(0, 0, this.W, rows * cell);
    g.globalAlpha = 0.85;
    g.strokeStyle = '#9fd4ff';
    g.lineWidth = Math.max(1.5, cell * 0.04);
    g.beginPath();
    const edge = rows * cell;
    for (let x = 0; x <= this.W; x += cell / 6) {
      const y = edge + (this.rm ? 0 : Math.sin(x / cell * 3 + this.t * 2.2) * cell * 0.05);
      if (x === 0) g.moveTo(x, y); else g.lineTo(x, y);
    }
    g.stroke();
    g.globalAlpha = 1;
  }

  /** Paso marcado: hacia dónde iría cada cuerpo y, si no se puede entrar, una cruz: la acción saldría de donde está. */
  drawStepGhost(g, cell) {
    const s = this.state;
    const step = this.previewStep;
    if (!step || !s.me) return;
    const color = MODE_COLORS.MOVE;
    const mark = (from, target, bounced) => {
      if (!target) return;
      const pulse = this.rm ? 0.5 : 0.5 + 0.5 * Math.sin(this.t * 6);
      g.strokeStyle = bounced ? '#ff6a5a' : color;
      g.fillStyle = bounced ? '#ff6a5a' : color;
      g.lineWidth = Math.max(2, cell * 0.05);
      g.setLineDash(DASH.map((d) => d * cell / 60));
      g.lineDashOffset = this.rm ? 0 : -this.t * cell * 0.5;
      g.globalAlpha = bounced ? 0.16 : 0.2 + pulse * 0.1;
      if (inBounds(target.x, target.y)) g.fillRect(target.x * cell + 2, target.y * cell + 2, cell - 4, cell - 4);
      g.globalAlpha = 1;
      if (inBounds(target.x, target.y)) g.strokeRect(target.x * cell + cell * 0.08, target.y * cell + cell * 0.08, cell * 0.84, cell * 0.84);
      g.setLineDash(NO_DASH);
      const ax = (from.x + 0.5) * cell;
      const ay = (from.y + 0.5) * cell;
      const bx = (target.x + 0.5) * cell;
      const by = (target.y + 0.5) * cell;
      g.beginPath();
      g.moveTo(ax, ay);
      g.lineTo(bx, by);
      g.stroke();
      if (bounced && inBounds(target.x, target.y)) {
        const r = cell * 0.18;
        g.beginPath();
        g.moveTo(bx - r, by - r); g.lineTo(bx + r, by + r);
        g.moveTo(bx + r, by - r); g.lineTo(bx - r, by + r);
        g.stroke();
      }
    };
    const start = this.origin();
    if (start) mark(start, step.target ?? step.pos, step.bounced);
    if (s.me.secondBody && step.secondTarget) mark(s.me.secondBody, step.secondTarget, step.secondBounced);
  }

  /** Corrimiento: mueve la línea del mapa (drawTiles y drawObjects la dibujan desplazada) y a quienes están encima. */
  playShift(shift, ids, B, A, startOf, t, S) {
    const tl = this.timeline;
    const dur = 480 * S;
    const anim = { ...shift, p: 0 };
    tl.at(t, () => {
      this.shiftAnim = anim;
      this.shake(0.4);
      let sub = shiftText(shift);
      const stairs = [...this.tiles].some(([k, kind]) => { const [x, y] = k.split(',').map(Number); return kind === 'STAIRS' && onShiftLine(shift, x, y); });
      if (stairs) sub += ': ¡la escalera se mueve!';
      this.banner = { text: 'La sala tiembla', sub, start: this.now, dur: 1700, color: '#d8ccb8' };
      for (let i = 0; i < SIZE; i++) {
        const x = shift.axis === 'row' ? i : shift.index;
        const y = shift.axis === 'row' ? shift.index : i;
        if (!this.rm && i % 2 === 0) this.particles.burst(x + 0.5, y + 0.9, 4, { speed: 1, life: 0.5, size: 0.05, colors: ['#8a7e70', '#6a5e50'], drag: 4, up: 0.3 });
      }
    });
    tl.tween(t, dur, (p) => { anim.p = ease.inOutQuad(p); }, () => { if (this.shiftAnim === anim) this.shiftAnim = null; });
    const axisKey = shift.axis === 'row' ? 'x' : 'y';
    for (const id of ids) {
      const v = this.ents.get(id);
      const d = B.get(id)?.d;
      if (!v || !d?.position || isSpirit(d) || !onShiftLine(shift, d.position.x, d.position.y)) continue;
      const from = d.position;
      const to = startOf(d);
      const wraps = Math.abs(to[axisKey] - from[axisKey]) > 1;
      const baseAlpha = v.alphaTarget;
      tl.tween(t, dur, (p) => {
        const k = ease.inOutQuad(p);
        const raw = from[axisKey] + shift.sign * k;
        // Quien cruza el borde se desvanece en uno y aparece por el otro.
        v[axisKey] = wraps && k > 0.5 ? raw - shift.sign * SIZE : raw;
        if (wraps) v.alpha = Math.abs(1 - 2 * k);
      }, () => {
        v[axisKey] = to[axisKey];
        if (wraps) { v.alpha = 1; v.alphaTarget = baseAlpha || 1; }
      });
    }
  }

  /** Desplazamiento con que se dibuja ahora una casilla: las de la línea corrida vienen de la casilla anterior. */
  shiftOffset(x, y) {
    const s = this.shiftAnim;
    if (!s || !onShiftLine(s, x, y)) return [0, 0];
    const k = (1 - s.p) * s.sign;
    return s.axis === 'row' ? [-k, 0] : [0, -k];
  }

  /** Volver: se deshace en luz fría y reaparece donde estaba hace 2 rondas. */
  rewindStep(v, from, to, t, S) {
    const tl = this.timeline;
    const colors = ['#6fd8c8', '#c8fff4', '#2f7f76'];
    tl.at(t, () => {
      this.particles.vortex(from.x + 0.5, from.y + 0.5, this.rm ? 6 : 22, { radius: 0.7, life: 0.5, colors, inward: true });
      v.alphaTarget = 0.1;
    });
    tl.tween(t + 120 * S, 200 * S, (p) => {
      v.x = from.x + (to.x - from.x) * ease.inOutQuad(p);
      v.y = from.y + (to.y - from.y) * ease.inOutQuad(p);
    }, () => {
      v.x = to.x; v.y = to.y;
      v.alphaTarget = 1;
      this.ring(to.x + 0.5, to.y + 0.5, '#6fd8c8', 0.9, 500);
      this.particles.vortex(to.x + 0.5, to.y + 0.5, this.rm ? 6 : 22, { radius: 0.2, life: 0.6, colors, inward: false });
      this.floaters.add('¡anteayer!', to.x + 0.5, to.y + 0.05, { color: '#c8fff4', size: 0.2 });
    });
  }

  /** Tensar: el hilo entre los dos cuerpos se vuelve una línea de luz que corta lo que haya en medio. */
  tensarFx(a, b) {
    this.addFx(520, (g, p, cell) => {
      g.globalCompositeOperation = 'lighter';
      g.globalAlpha = 1 - p;
      g.lineCap = 'round';
      for (const [w, c] of [[0.2, HERO_COLORS.HILVAN], [0.07, '#ffffff']]) {
        g.strokeStyle = c;
        g.lineWidth = cell * w * (1 - p * 0.5);
        g.beginPath();
        g.moveTo((a.x + 0.5) * cell, (a.y + 0.55) * cell);
        g.lineTo((b.x + 0.5) * cell, (b.y + 0.55) * cell);
        g.stroke();
      }
      g.lineCap = 'butt';
      g.globalCompositeOperation = 'source-over';
      g.globalAlpha = 1;
    });
    this.shake(0.25);
  }

  moveTween(v, from, to, t, dur, hop) {
    this.timeline.tween(t, dur, (p) => {
      const k = hop ? ease.inOutQuad(p) : ease.outCubic(p);
      v.x = from.x + (to.x - from.x) * k;
      v.y = from.y + (to.y - from.y) * k;
      v.hop = hop * ease.pulse(p);
    }, () => {
      v.x = to.x; v.y = to.y; v.hop = 0;
      if (!this.rm && v.d.kind !== 'POLILLA') {
        this.particles.burst(to.x + 0.5, to.y + 0.86, hop > 0.3 ? 10 : 5, { speed: 1.1, life: 0.45, size: 0.05, colors: ['#6a5e50', '#4a4036'], drag: 5, up: 0.4 });
      }
    });
  }

  /** Deriva de la Brasa: flota en línea recta, atravesando lo que haya, y deja una estela azul. */
  driftTween(v, from, to, t, dur) {
    this.timeline.tween(t, dur, (p) => {
      const k = ease.inOutQuad(p);
      v.x = from.x + (to.x - from.x) * k;
      v.y = from.y + (to.y - from.y) * k;
      v.hop = 0.12 * ease.pulse(p);
      if (!this.rm && Math.random() < 0.8) {
        this.particles.spawn(v.x + 0.35 + Math.random() * 0.3, v.y + 0.35 + Math.random() * 0.35, (Math.random() - 0.5) * 0.3, -0.4 - Math.random() * 0.4,
          0.7, 0.06, SPIRIT_COLORS[Math.floor(Math.random() * 3)], 0, 1.2, true);
      }
    }, () => { v.x = to.x; v.y = to.y; v.hop = 0; });
  }

  /** Brizna de luz de la Brasa hacia su objetivo (Avivar, Estorbo, Reavivar). */
  wisp(from, to, dur, colors = SPIRIT_COLORS) {
    const fx = from.x + 0.5;
    const fy = from.y + 0.35;
    const tx = to.x + 0.5;
    const ty = to.y + 0.45;
    this.addFx(dur, (g, p, cell) => {
      const k = ease.inOutQuad(p);
      const x = fx + (tx - fx) * k + Math.sin(p * Math.PI * 3) * 0.08;
      const y = fy + (ty - fy) * k - Math.sin(Math.PI * p) * 0.35;
      if (!this.rm) this.particles.spawn(x, y, (Math.random() - 0.5) * 0.3, (Math.random() - 0.5) * 0.3, 0.45, 0.07, colors[Math.floor(Math.random() * colors.length)], 0, 2, true);
      g.globalCompositeOperation = 'lighter';
      const r = cell * 0.36;
      g.drawImage(glow(colors[0]), x * cell - r, y * cell - r, r * 2, r * 2);
      g.globalCompositeOperation = 'source-over';
      g.fillStyle = '#ffffff';
      const s = cell * 0.08;
      g.fillRect(x * cell - s / 2, y * cell - s / 2, s, s);
    });
  }

  smoke(x, y) {
    this.particles.burst(x, y, this.rm ? 6 : 18, { speed: 1.2, life: 0.8, size: 0.14, colors: ['#3a2850', '#4c3a66', '#241a30'], drag: 3, up: 0.5 });
  }

  /** Acción de un héroe y lo que provoca; devuelve cuándo termina. */
  playHeroGroup(actorId, list, t0, S, A, B) {
    const tl = this.timeline;
    const v = this.ents.get(actorId);
    const actor = (A.get(actorId) ?? B.get(actorId))?.d;
    const action = list.find((e) => ACTIONS.has(e.type));
    const rest = list.filter((e) => e !== action);
    if (!v || !actor) {
      rest.forEach((ev, i) => tl.at(t0 + i * 90 * S, () => this.applyEvent(ev)));
      if (action) tl.at(t0, () => this.applyEvent(action));
      return t0 + (list.length * 90 + 100) * S;
    }
    const facing = actor.facing ?? v.facing;
    const dir = DIRS[facing] ?? [0, 1];
    const pos = actor.position ?? { x: v.x, y: v.y };
    const firstTarget = rest.find((e) => (e.type === 'DAMAGED' || e.type === 'BLOCKED') && e.targetId);
    let impact = t0;
    if (action) tl.at(t0, () => { v.facing = facing; });

    switch (action?.type) {
      case 'ATTACKED': {
        const range = heroInfo(actor.hero).range ?? 1;
        if (range > 1) {
          // A distancia: la nota de La Escucha o el tañido del Campanero.
          const target = firstTarget ? this.ents.get(firstTarget.targetId) : null;
          const end = target ? { x: target.x, y: target.y } : this.rayEnd(pos, dir, range);
          const dist = Math.abs(end.x - pos.x) + Math.abs(end.y - pos.y);
          const dur = (90 + dist * 70) * S;
          const [core, trail] = actor.hero === 'ESCUCHA' ? ['#dff0ff', '#8a7ed8'] : ['#ffe9a0', '#d8a040'];
          tl.at(t0, () => this.bolt(pos, end, dur, core, trail, !target));
          impact = t0 + dur;
        } else {
          this.lunge(v, dir, t0, S);
          tl.at(t0 + 100 * S, () => this.slash(pos.x + dir[0] + 0.5, pos.y + dir[1] + 0.5, facing, HERO_COLORS[actor.hero]));
          // Hilván ataca con los dos cuerpos, en espejo.
          const second = this.ents.get(secondId(actorId));
          if (second && !isSpirit(actor)) {
            const mirrorFacing = mirrored(facing);
            const mirrorDir = DIRS[mirrorFacing] ?? dir;
            const at = A.get(secondId(actorId))?.d.position ?? { x: second.x, y: second.y };
            this.lunge(second, mirrorDir, t0, S);
            tl.at(t0 + 100 * S, () => this.slash(at.x + mirrorDir[0] + 0.5, at.y + mirrorDir[1] + 0.5, mirrorFacing, HERO_COLORS.HILVAN));
          }
          impact = t0 + 110 * S;
        }
        break;
      }
      case 'ABILITY_USED':
        if (actor.hero === 'HILVAN' && actor.secondBody) {
          tl.at(t0, () => this.tensarFx(actor.position, actor.secondBody));
          impact = t0 + 180 * S;
        } else if (actor.hero === 'USURERA') {
          // La moneda vuela hasta quien se endeuda.
          const healed = rest.find((e) => (e.type === 'HEALED' || e.type === 'REVIVED') && e.targetId);
          const target = healed ? this.ents.get(healed.targetId) : null;
          const end = target ? { x: target.x, y: target.y } : this.rayEnd(pos, dir, 2);
          const dur = 240 * S;
          tl.at(t0, () => this.wisp(pos, end, dur, ['#ffe07a', '#e8c44a', '#ff9a3a']));
          impact = t0 + dur;
        } else if (actor.hero === 'ERRATA') {
          tl.at(t0, () => this.ring(pos.x + 0.5, pos.y + 0.5, '#c58bff', 2.4, 500));
          impact = t0 + 120 * S;
        } else {
          impact = t0 + 60 * S;
        }
        break;
      case 'GUARDED':
      case 'DEFAULT_GUARD_APPLIED':
        tl.at(t0, () => this.guardBubble(v, action.type === 'DEFAULT_GUARD_APPLIED'));
        impact = t0 + 120 * S;
        break;
      case 'CHEST_OPENED':
        this.lunge(v, dir, t0, S * 0.8);
        tl.at(t0 + 110 * S, () => this.applyEvent(action, actor));
        impact = t0 + 260 * S;
        break;
      case 'SHIELD_KINDLED':
      case 'ATTACK_HINDERED': {
        // La Brasa lanza una brizna hasta 2 casillas; sin objetivo se apaga al final del tramo.
        const target = action.targetId ? this.ents.get(action.targetId) : null;
        const reach = { x: Math.max(0, Math.min(SIZE - 1, pos.x + dir[0] * 2)), y: Math.max(0, Math.min(SIZE - 1, pos.y + dir[1] * 2)) };
        const end = target ? { x: target.x, y: target.y } : reach;
        const dur = (140 + manhattan(pos, end) * 90) * S;
        const colors = action.type === 'ATTACK_HINDERED' ? ['#b89aff', '#e6dcff', '#6fc8ff'] : SPIRIT_COLORS;
        tl.at(t0, () => this.wisp(pos, end, dur, colors));
        tl.at(t0 + dur, () => this.applyEvent(action, actor, false, end));
        impact = t0 + dur + 60 * S;
        break;
      }
      case 'BEACON_LIT':
        tl.at(t0, () => this.applyEvent(action, actor));
        impact = t0 + 320 * S;
        break;
      case 'REVIVED':
      case 'BLESSING_GAINED': {
        const target = action.type === 'REVIVED' && isSpirit(actor) && action.targetId ? this.ents.get(action.targetId) : null;
        if (target) {
          // Reavivar: la Brasa le sopla su luz al caído.
          const dur = 280 * S;
          tl.at(t0, () => this.wisp(pos, { x: target.x, y: target.y }, dur, ['#ffe9a0', '#6fc8ff', '#ffffff']));
          tl.at(t0 + dur, () => this.applyEvent(action, actor));
          impact = t0 + dur + 300 * S;
          break;
        }
        tl.at(t0, () => this.applyEvent(action, actor));
        impact = t0 + 300 * S;
        break;
      }
      default:
        break;
    }
    rest.forEach((ev, i) => tl.at(impact + i * 90 * S, () => this.applyEvent(ev, actor)));
    return impact + rest.length * 90 * S + 160 * S;
  }

  lunge(v, dir, t, S) {
    this.timeline.tween(t, 230 * S, (p) => {
      const k = (p < 0.45 ? ease.outCubic(p / 0.45) : 1 - ease.inOutQuad((p - 0.45) / 0.55)) * 0.36;
      v.ox = dir[0] * k; v.oy = dir[1] * k;
    }, () => { v.ox = 0; v.oy = 0; });
  }

  rayEnd(pos, dir, len) {
    let end = { x: pos.x, y: pos.y };
    for (let i = 1; i <= len; i++) {
      const x = pos.x + dir[0] * i;
      const y = pos.y + dir[1] * i;
      if (!inBounds(x, y) || STOPS_SHOTS.has(this.tiles.get(tileKey(x, y)))) {
        return { x: end.x + dir[0] * 0.45, y: end.y + dir[1] * 0.45 };
      }
      end = { x, y };
    }
    return end;
  }

  /** Golpe de lo anunciado por un enemigo: destello en sus casillas y su ataque (embestida o virote). */
  enemyStrike({ id, d }, t, S) {
    const tl = this.timeline;
    const v = this.ents.get(id);
    const tiles = d.telegraph;
    tl.at(t, () => {
      this.addFx(380, (g, p, cell) => {
        g.globalAlpha = (1 - p) * 0.75;
        g.fillStyle = '#ff3a2a';
        for (const q of tiles) g.fillRect(q.x * cell, q.y * cell, cell, cell);
        g.globalAlpha = 1;
      });
      if (d.kind === 'CELADOR') this.shake(0.35);
    });
    if (!v || v.dead) return;
    const first = tiles[0];
    const dx = Math.sign(first.x - d.position.x);
    const dy = Math.sign(first.y - d.position.y);
    if (d.kind === 'BALLESTERO') {
      const last = tiles[tiles.length - 1];
      tl.at(t, () => this.bolt(d.position, last, 170 * S, '#e8e0cc', '#a8a090', false, true));
    } else if (d.kind !== 'TOTEM') {
      this.lunge(v, [dx, dy], t, S);
    }
  }

  // ───────────── sucesos ─────────────

  /** `fromFloor`: llega tras bajar de piso (renacer o aparición); `aim`: adónde apuntó la Brasa si no hubo objetivo. */
  applyEvent(ev, actorData = null, fromFloor = false, aim = null) {
    const target = ev.targetId ? this.ents.get(ev.targetId) : null;
    const actor = ev.actorId ? this.ents.get(ev.actorId) : null;
    const rm = this.rm;
    if (this.applyPathEvent(ev, target, actor, fromFloor, aim)) return;
    if (applyHeroEvent(this, ev, target, actor)) return;
    switch (ev.type) {
      case 'PUSHED': {
        if (!target) return;
        const [cx, cy] = this.center(target);
        this.floaters.add('¡empujón!', cx, cy - 0.35, { color: '#ffb45a', size: 0.24 });
        this.particles.burst(cx, cy, rm ? 4 : 12, { speed: 2, life: 0.4, size: 0.05, colors: ['#ffb45a', '#fff0c0'], drag: 3 });
        this.shake(0.18);
        break;
      }
      case 'ROOM_FLOODED':
        this.screenFlash('#2f7fd8', 0.3);
        this.banner = { text: 'El agua sube', sub: `${ev.amount ?? 1} fila${ev.amount === 1 ? '' : 's'} inundada${ev.amount === 1 ? '' : 's'}`, start: this.now, dur: 1400, color: '#9fd4ff' };
        break;
      case 'DAMAGED': {
        if (!target) return;
        const amount = ev.amount ?? 0;
        const [cx, cy] = this.center(target);
        if (amount > 0 && !ev.actorId && ev.phase === 'ACTIONS' && target.type === 'hero') {
          // Sabotaje: la víctima se ve bien, el autor no existe.
          this.floaters.add('¿quién?', cx, cy - 0.68, { color: '#d9b3ff', size: 0.26, life: 1.4 });
          this.ring(target.x, target.y, '#c58af0', 1.1, 600);
          this.screenFlash('#7a2ad0', target.isMe ? 0.4 : 0.14);
          this.shake(0.3);
        }
        if (amount <= 0) { this.floaters.add('0', cx, cy - 0.3, { color: '#c8c0b0', size: 0.28 }); return; }
        target.hp = Math.max(0, target.hp - amount);
        target.flash = rm ? 0.5 : 1;
        target.jolt = rm ? 0 : 0.25;
        this.floaters.add(`-${amount}`, cx, cy - 0.3, { color: target.type === 'hero' ? '#ff6a5a' : '#ffd08a', size: 0.34 + Math.min(3, amount) * 0.04 });
        const colors = target.type === 'enemy' ? ['#ff5a3a', '#ffd08a', '#8a1a1a'] : ['#ff8a5a', '#fff0c0', '#d23a2a'];
        this.particles.burst(cx, cy, rm ? 5 : 14, { speed: 2.4, life: 0.45, size: 0.06, colors, drag: 3, glow: true });
        if (target.isMe) { this.shake(0.45); this.screenFlash('#ff2a1a', 0.5); } else this.shake(0.16);
        break;
      }
      case 'BLOCKED': {
        if (!target) return;
        const [cx, cy] = this.center(target);
        this.guardBubble(target, false, '#9ad8ff');
        this.particles.burst(cx, cy - 0.1, rm ? 4 : 12, { speed: 2, life: 0.35, size: 0.05, colors: ['#dff4ff', '#8fd0ff'], drag: 4, glow: true });
        this.floaters.add('¡bloqueo!', cx, cy - 0.35, { color: '#9ad8ff', size: 0.22 });
        this.shake(0.1);
        break;
      }
      case 'ENEMY_KILLED': {
        if (!target) return;
        this.dissolve(target);
        this.shake(target.d.kind === 'CELADOR' ? 0.9 : 0.22);
        if (target.d.kind === 'CELADOR') this.screenFlash('#ffd36a', 0.8);
        break;
      }
      case 'HEALED': {
        if (!target) return;
        const amount = ev.amount ?? 0;
        target.hp = Math.min(target.maxHp, target.hp + amount);
        target.trail = Math.min(target.trail, target.hp);
        const [cx, cy] = this.center(target);
        for (let i = 0; i < (rm ? 5 : 16); i++) {
          this.particles.spawn(cx - 0.3 + Math.random() * 0.6, cy + 0.2 - Math.random() * 0.4, (Math.random() - 0.5) * 0.3, -0.6 - Math.random() * 0.8, 0.9, 0.06, i % 2 ? '#8fff9a' : '#d8ffb0', 0, 0.5, true);
        }
        if (amount) this.floaters.add(`+${amount}`, cx, cy - 0.3, { color: '#7dff9a', size: 0.32 });
        break;
      }
      case 'EMBERS_GAINED':
      case 'EMBERS_STOLEN': {
        const who = actor ?? target;
        if (!who) return;
        const amount = ev.amount ?? 0;
        const [cx, cy] = this.center(who);
        const stolen = ev.type === 'EMBERS_STOLEN';
        const text = `${amount > 0 ? '+' : ''}${amount} ✦`;
        const color = stolen ? '#d48aff' : '#ffd36a';
        this.floaters.add(text, cx, cy - 0.35, { color, size: 0.3, life: who.isMe && amount > 0 ? 1.4 : 1.1, fly: who.isMe && amount > 0 && !rm, tx: SIZE - 0.5, ty: 0.25 });
        this.particles.burst(cx, cy - 0.2, rm ? 4 : 12, { speed: 1.5, life: 0.7, size: 0.05, colors: stolen ? ['#d48aff', '#8a4ad0'] : ['#ffd36a', '#fff3b0', '#ff9a3a'], drag: 2, glow: true, up: 0.6 });
        break;
      }
      case 'DOWNED': {
        if (!target) return;
        target.downTarget = 1;
        const [cx, cy] = this.center(target);
        this.floaters.add('¡derribado!', cx, cy - 0.4, { color: '#ff8a7a', size: 0.24, life: 1.4 });
        this.particles.burst(cx, cy, rm ? 4 : 14, { speed: 1, life: 0.9, size: 0.07, colors: ['#5a4a44', '#8a7a70'], drag: 2, up: 0.4 });
        if (target.isMe) { this.shake(0.6); this.screenFlash('#ff2a1a', 0.8); }
        break;
      }
      case 'EXTINGUISHED': {
        if (!target) return;
        target.downTarget = 1;
        target.alphaTarget = 0.35;
        const [cx, cy] = this.center(target);
        for (let i = 0; i < (rm ? 6 : 24); i++) {
          this.particles.spawn(cx - 0.3 + Math.random() * 0.6, cy - Math.random() * 0.3, (Math.random() - 0.5) * 0.4, -0.3 - Math.random() * 0.7, 1.6, 0.1, i % 3 ? '#4a4448' : '#ff6a2a', 0, 0.6, i % 3 === 0);
        }
        if (ev.amount) this.floaters.add(`${ev.amount} ✦`, cx, cy - 0.4, { color: '#a8a0a0', size: 0.28, life: 1.5 });
        this.floaters.add('murió', cx, cy - 0.05, { color: '#c8c0c0', size: 0.18, life: 1.6, delay: 0.2 });
        if (target.isMe) { this.shake(0.7); this.screenFlash('#000000', 0.9); }
        break;
      }
      case 'REVIVED': {
        if (!target) return;
        target.downTarget = 0;
        const [cx, cy] = this.center(target);
        this.pillar(cx, cy, '#ffe9a0');
        this.particles.burst(cx, cy, rm ? 5 : 18, { speed: 1.6, life: 0.9, size: 0.06, colors: ['#ffe9a0', '#fff', '#ffcf4a'], drag: 2, glow: true, up: 0.8 });
        this.floaters.add('¡de pie!', cx, cy - 0.4, { color: '#ffe9a0', size: 0.24 });
        break;
      }
      case 'CHEST_OPENED': {
        const at = ev.position ?? this.findChest(actorData?.position ?? (actor ? { x: Math.round(actor.x), y: Math.round(actor.y) } : null));
        if (!at) return;
        this.tileShown.delete(tileKey(at.x, at.y));
        const cx = at.x + 0.5;
        const cy = at.y + 0.5;
        this.addFx(700, (g, p, cell) => {
          const r = cell * (0.4 + p * 0.9);
          g.globalCompositeOperation = 'lighter';
          g.globalAlpha = (1 - p) * 0.8;
          g.drawImage(glow('#ffcf4a'), cx * cell - r, cy * cell - r, r * 2, r * 2);
          g.globalCompositeOperation = 'source-over';
          g.globalAlpha = 1;
        });
        for (let i = 0; i < (rm ? 6 : 26); i++) {
          const a = -Math.PI / 2 + (Math.random() - 0.5) * 1.6;
          const s = 1.5 + Math.random() * 2.2;
          this.particles.spawn(cx, cy - 0.1, Math.cos(a) * s, Math.sin(a) * s, 0.9, 0.055, EMBER_COLORS[i % 3], 4, 1, true);
        }
        this.shake(0.12);
        break;
      }
      case 'BLESSING_GAINED': {
        const who = actor;
        if (!who) return;
        const [cx, cy] = this.center(who);
        this.addFx(1000, (g, p, cell) => {
          g.globalCompositeOperation = 'lighter';
          for (let i = 0; i < 6; i++) {
            const a = p * 7 + i * (Math.PI / 3);
            const r = (0.5 - p * 0.3) * cell;
            g.globalAlpha = (1 - p) * 0.9;
            g.fillStyle = '#ffe9a0';
            const s = cell * 0.06;
            g.fillRect(cx * cell + Math.cos(a) * r - s / 2, (cy - p * 0.5) * cell + Math.sin(a) * r * 0.5 - s / 2, s, s);
          }
          g.globalCompositeOperation = 'source-over';
          g.globalAlpha = 1;
        });
        this.floaters.add('mejora', cx, cy - 0.45, { color: '#ffe9a0', size: 0.2 });
        break;
      }
      case 'ENEMY_SPAWNED': {
        const p = ev.position ?? (target ? { x: target.x, y: target.y } : null);
        if (!p) return;
        this.particles.burst(p.x + 0.5, p.y + 0.6, rm ? 6 : 20, { speed: 1.4, life: 0.8, size: 0.12, colors: ['#5a2a6a', '#8a3a9a', '#2a1030'], drag: 3, up: 0.5 });
        this.ring(p.x + 0.5, p.y + 0.6, '#b04ad0', 0.6, 500);
        if (target) { target.alpha = 0; target.alphaTarget = 1; }
        break;
      }
      case 'TRAP_TRIGGERED': {
        const p = ev.position;
        if (!p) return;
        this.tileShown.delete(tileKey(p.x, p.y));
        this.addFx(520, (g, q, cell) => this.drawSpikes(g, p.x, p.y, cell, ease.outBack(Math.min(1, q * 3)) * (1 - Math.max(0, q - 0.7) / 0.3), 1));
        this.particles.burst(p.x + 0.5, p.y + 0.5, rm ? 4 : 12, { speed: 2.2, life: 0.35, size: 0.04, colors: ['#e8ecf2', '#a8b0bc'], drag: 4, glow: true });
        this.shake(0.15);
        break;
      }
      case 'FIRE_STARTED': {
        const p = ev.position;
        if (!p) return;
        this.tileShown.delete(tileKey(p.x, p.y));
        for (let i = 0; i < (rm ? 6 : 22); i++) {
          this.particles.spawn(p.x + 0.2 + Math.random() * 0.6, p.y + 0.8, (Math.random() - 0.5) * 0.8, -1 - Math.random() * 1.6, 0.7, 0.08, EMBER_COLORS[i % 3], 0, 1.5, true);
        }
        this.ring(p.x + 0.5, p.y + 0.5, '#ff7a2a', 0.7, 400);
        break;
      }
      case 'FROZEN': {
        if (!target) return;
        target.frozen = true;
        const [cx, cy] = this.center(target);
        this.particles.burst(cx, cy, rm ? 5 : 16, { speed: 1.8, life: 0.6, size: 0.06, colors: ['#dff6ff', '#9fe0ff', '#ffffff'], drag: 3, glow: true });
        this.floaters.add('❄ congelado', cx, cy - 0.4, { color: '#bfeaff', size: 0.2 });
        break;
      }
      case 'BOSS_PHASE_CHANGED': {
        const boss = target ?? [...this.ents.values()].find((e) => e.d.kind === 'CELADOR');
        const [cx, cy] = boss ? this.center(boss) : [SIZE / 2, SIZE / 2];
        this.shake(1);
        this.screenFlash('#ff2a1a', 1);
        this.ring(cx, cy, '#ff3a2a', 4.5, 900);
        this.ring(cx, cy, '#ffcf4a', 3, 700);
        this.particles.burst(cx, cy, rm ? 10 : 40, { speed: 4, life: 0.9, size: 0.08, colors: ['#ff3a2a', '#ffcf4a', '#8a1010'], drag: 2, glow: true });
        this.banner = { text: '¡El Celador se enfurece!', sub: 'Fase 2: invoca tótems y ataca en cruz', start: this.now, dur: 2400, color: '#ff6a5a' };
        break;
      }
      default:
        break;
    }
  }

  /** Sucesos de los caminos tras la extinción; devuelve true si lo manejó. */
  applyPathEvent(ev, target, actor, fromFloor, aim) {
    const rm = this.rm;
    const me = this.state.me;
    const nameOf = (id) => (id === me?.playerId ? null : this.state.allies?.find((a) => a.playerId === id)?.displayName ?? 'Un portador');
    switch (ev.type) {
      case 'PATH_CHOSEN': {
        const friendship = ev.detail !== 'VENGEANCE';
        const mine = ev.actorId === me?.playerId;
        if (actor) {
          const [cx, cy] = this.center(actor);
          if (friendship) {
            // El cuerpo tendido se vuelve llama azul.
            if (actor.revealTo) { actor.d = actor.revealTo; actor.revealTo = null; }
            actor.downT = 0; actor.downTarget = 0; actor.alphaTarget = 1;
            this.pillar(cx, cy, '#6fc8ff');
            this.particles.vortex(cx, cy, rm ? 8 : 26, { radius: 0.9, life: 0.8, colors: SPIRIT_COLORS, inward: true });
            this.floaters.add('Ayudar', cx, cy - 0.45, { color: '#c8ecff', size: 0.22, life: 1.4 });
          } else {
            // Se hunde en humo rojo: volverá del lado de la mazmorra.
            this.particles.vortex(cx, cy + 0.1, rm ? 8 : 28, { radius: 0.2, life: 1, colors: ['#3a0604', '#a8160e', '#ff4a3a'], inward: false, glow: false });
            this.ring(cx, cy + 0.3, '#ff4a3a', 0.9, 600);
            this.floaters.add('Vengarse', cx, cy - 0.45, { color: '#ff9a8c', size: 0.22, life: 1.4 });
          }
        }
        const who = nameOf(ev.actorId);
        this.banner = mine
          ? { text: friendship ? 'Ayudar' : 'Vengarse', sub: friendship ? 'Eres un Fantasma: ayuda al grupo y gana puntos de apoyo' : 'Volverás como Traidor, del lado de la mazmorra', start: this.now, dur: 2600, color: friendship ? '#9ad8ff' : '#ff8a7a' }
          : { text: friendship ? `${who}: Ayudar` : `${who}: Vengarse`, sub: friendship ? 'Vaga como Fantasma y ayuda al grupo' : 'Volverá como Traidor', start: this.now, dur: 2000, color: friendship ? '#9ad8ff' : '#ff8a7a' };
        if (mine) this.screenFlash(friendship ? '#6fc8ff' : '#a8160e', 0.6);
        return true;
      }
      case 'SHIELD_KINDLED': {
        if (!target) {
          const at = aim ?? (actor ? { x: actor.x, y: actor.y } : null);
          if (at) this.particles.burst(at.x + 0.5, at.y + 0.5, rm ? 3 : 10, { speed: 1, life: 0.5, size: 0.05, colors: SPIRIT_COLORS, drag: 3, glow: true });
          return true;
        }
        const [cx, cy] = this.center(target);
        this.guardBubble(target, false, '#6fc8ff');
        this.particles.vortex(cx, cy, rm ? 6 : 18, { radius: 0.55, life: 0.6, colors: SPIRIT_COLORS, inward: true });
        this.floaters.add('+1 ⛨', cx, cy - 0.38, { color: '#9ad8ff', size: 0.26 });
        return true;
      }
      case 'ATTACK_HINDERED': {
        if (!target) {
          const at = aim ?? (actor ? { x: actor.x, y: actor.y } : null);
          if (at) {
            this.particles.burst(at.x + 0.5, at.y + 0.5, rm ? 3 : 10, { speed: 1, life: 0.5, size: 0.05, colors: ['#b89aff', '#e6dcff'], drag: 3, glow: true });
            this.floaters.add('sin efecto', at.x + 0.5, at.y + 0.2, { color: '#c8c0d8', size: 0.18 });
          }
          return true;
        }
        const [cx, cy] = this.center(target);
        // Cadenas de luz que giran alrededor del enemigo y un reloj de arena.
        this.addFx(900, (g, p, cell) => {
          g.globalCompositeOperation = 'lighter';
          for (let i = 0; i < 8; i++) {
            const a = p * 9 + (i * Math.PI) / 4;
            const r = cell * (0.55 - p * 0.15);
            g.globalAlpha = (1 - p) * 0.9;
            g.fillStyle = i % 2 ? '#b89aff' : '#e6dcff';
            const s = cell * 0.07;
            g.fillRect(cx * cell + Math.cos(a) * r - s / 2, cy * cell + Math.sin(a) * r * 0.6 - s / 2, s, s);
          }
          g.globalCompositeOperation = 'source-over';
          g.globalAlpha = 1;
        });
        target.frozen = false;
        target.jolt = rm ? 0 : 0.2;
        this.floaters.add('⧖ ¡frenado!', cx, cy - 0.42, { color: '#d8c8ff', size: 0.22, life: 1.3 });
        return true;
      }
      case 'BEACON_LIT': {
        const at = ev.position ?? (actor ? { x: Math.round(actor.x), y: Math.round(actor.y) } : null);
        if (!at) return true;
        this.beacons.push({ x: at.x, y: at.y, start: this.now });
        const cx = at.x + 0.5;
        const cy = at.y + 0.5;
        this.ring(cx, cy, '#ffe9a0', BEACON_RADIUS + 0.5, 700);
        this.ring(cx, cy, '#6fc8ff', BEACON_RADIUS, 600);
        this.particles.burst(cx, cy - 0.2, rm ? 5 : 18, { speed: 2, life: 0.8, size: 0.05, colors: ['#ffe9a0', '#c8ecff', '#6fc8ff'], drag: 2, glow: true, up: 0.6 });
        return true;
      }
      case 'WARMTH_HEALED': {
        if (!actor || !target) return true;
        const [ax, ay] = this.center(actor);
        const [tx, ty] = this.center(target);
        this.particles.stream(ax, ay, tx, ty, rm ? 6 : 22, { colors: ['#ffcf6a', '#6fc8ff', '#ffe9a0'], life: 0.9, size: 0.06 });
        this.floaters.add('♨ calidez', tx, ty - 0.5, { color: '#ffd9a0', size: 0.2, delay: 0.15 });
        return true;
      }
      case 'ECHO_GAINED': {
        const who = actor ?? (me ? this.ents.get(me.playerId) : null);
        if (!who) return true;
        const [cx, cy] = this.center(who);
        const n = ev.amount ?? 1;
        this.floaters.add(`+${n} de apoyo`, cx, cy - 0.5, { color: '#9ad8ff', size: 0.24, life: 1.4 });
        this.particles.vortex(cx, cy, rm ? 5 : 14, { radius: 0.5, life: 0.6, size: 0.05, colors: SPIRIT_COLORS, inward: true });
        return true;
      }
      case 'EMBER_REBORN': {
        const v = actor;
        const at = ev.position ?? (v ? { x: v.x, y: v.y } : null);
        if (v) { v.locked = false; v.alpha = 0; v.alphaTarget = 1; v.downT = 0; v.downTarget = 0; }
        if (!at) return true;
        const cx = at.x + 0.5;
        const cy = at.y + 0.5;
        this.pillar(cx, cy, '#6fc8ff');
        this.pillar(cx, cy, '#ffcf6a');
        this.ring(cx, cy + 0.2, '#ffe9a0', 1.2, 800);
        this.particles.vortex(cx, cy, rm ? 8 : 30, { radius: 1.1, life: 0.9, colors: [...SPIRIT_COLORS, '#ffcf6a'], inward: true });
        this.particles.burst(cx, cy, rm ? 6 : 24, { speed: 2.4, life: 1, size: 0.06, colors: ['#ffe9a0', '#ffcf6a', '#ff9a3a'], drag: 2, glow: true, up: 0.8 });
        this.floaters.add('¡renace!', cx, cy - 0.5, { color: '#ffe9a0', size: 0.26, life: 1.5 });
        if (ev.actorId === me?.playerId) { this.screenFlash('#ffe9a0', 0.7); this.shake(0.25); }
        if (!fromFloor && ev.actorId !== me?.playerId) {
          this.banner = { text: `${nameOf(ev.actorId)} renace`, sub: 'El Fantasma vuelve como héroe', start: this.now, dur: 1800, color: '#ffe9a0' };
        }
        return true;
      }
      case 'RENEGADE_SPAWNED': {
        const v = actor;
        const at = ev.position ?? (v ? { x: v.x, y: v.y } : null);
        if (v) { v.locked = false; v.alpha = 0; v.alphaTarget = 1; }
        const mine = ev.actorId === me?.playerId;
        if (at) {
          const cx = at.x + 0.5;
          const cy = at.y + 0.6;
          this.addFx(1100, (g, p, cell) => {
            // Portal: un óvalo rojo oscuro que se abre y se cierra.
            const k = ease.pulse(Math.min(1, p * 1.2));
            g.globalAlpha = k * 0.85;
            g.fillStyle = '#1a0302';
            g.beginPath();
            g.ellipse(cx * cell, cy * cell, cell * 0.5 * k, cell * 0.2 * k, 0, 0, Math.PI * 2);
            g.fill();
            g.globalCompositeOperation = 'lighter';
            g.globalAlpha = k * 0.7;
            g.drawImage(glow('#ff2a1a'), (cx - 0.9) * cell, (cy - 0.9) * cell, cell * 1.8, cell * 1.8);
            g.globalCompositeOperation = 'source-over';
            g.globalAlpha = 1;
          });
          this.particles.vortex(cx, cy, rm ? 8 : 30, { radius: 0.2, life: 1.1, size: 0.07, colors: BLOOD_COLORS, inward: false });
          this.ring(cx, cy, '#ff4a3a', 1.1, 700);
          this.floaters.add('¡Traidor!', cx, cy - 0.7, { color: '#ff8a7a', size: 0.26, life: 1.5 });
        }
        this.shake(0.3);
        this.screenFlash('#a8160e', mine ? 0.7 : 0.4);
        // Cada héroe activo gana +1 de escudo al aparecer un Renegado.
        const meV = me ? this.ents.get(me.playerId) : null;
        if (!mine && me?.state === 'ALIVE' && meV) {
          this.guardBubble(meV, false, '#8fd0ff');
          const [mx, my] = this.center(meV);
          this.floaters.add('+1 ⛨', mx, my - 0.4, { color: '#9ad8ff', size: 0.24, delay: 0.3 });
        }
        if (!fromFloor) {
          this.banner = mine
            ? { text: 'Vuelves como Traidor', sub: 'Del lado de la mazmorra: los monstruos no te atacan', start: this.now, dur: 2400, color: '#ff8a7a' }
            : { text: '¡Un Traidor aparece!', sub: `${nameOf(ev.actorId)} vuelve del lado de la mazmorra`, start: this.now, dur: 2200, color: '#ff8a7a' };
        }
        return true;
      }
      case 'RENEGADE_DEFEATED': {
        if (!target) return true;
        const [cx, cy] = this.center(target);
        this.ring(cx, cy, '#ff4a3a', 1.2, 600);
        this.particles.burst(cx, cy, rm ? 8 : 30, { speed: 3, life: 0.9, size: 0.07, colors: [...BLOOD_COLORS, '#2a0503'], drag: 2, glow: true });
        this.floaters.add('¡Traidor derrotado!', cx, cy - 0.5, { color: '#ffd08a', size: 0.22, life: 1.5 });
        target.downTarget = 1;
        target.alphaTarget = 0.35;
        this.shake(0.4);
        if (target.isMe) this.screenFlash('#000000', 0.9);
        return true;
      }
      case 'BLOCKED': {
        if (ev.detail !== 'WARNING_SHIELD' || !target) return false;
        // Escudo de aviso: el primer golpe del Renegado se quiebra contra un escudo rojo.
        const [cx, cy] = this.center(target);
        this.guardBubble(target, false, '#ff8a7a');
        this.particles.burst(cx, cy - 0.1, rm ? 5 : 16, { speed: 2.4, life: 0.45, size: 0.05, colors: ['#ffe0d8', '#ff8a7a', '#ffffff'], drag: 4, glow: true });
        this.floaters.add('escudo de aviso', cx, cy - 0.4, { color: '#ffb0a0', size: 0.2, life: 1.3 });
        this.shake(0.15);
        return true;
      }
      default:
        return false;
    }
  }

  center(v) { return [v.x + 0.5 + v.ox, v.y + 0.5 + v.oy - v.hop]; }

  findChest(near) {
    if (!near) return null;
    for (const [k, kind] of this.tileShown) {
      if (kind !== 'CHEST' || this.tiles.get(k) !== 'CHEST_OPENED') continue;
      const [x, y] = k.split(',').map(Number);
      if (Math.abs(x - near.x) + Math.abs(y - near.y) <= 1) return { x, y };
    }
    return null;
  }

  // ───────────── efectos ─────────────

  addFx(dur, draw) {
    this.effects.push({ start: this.now, dur: this.rm ? Math.min(dur, 400) : dur, draw });
  }

  shake(amount) {
    if (this.rm) return;
    this.trauma = Math.min(1, this.trauma + amount);
  }

  screenFlash(color, strength) {
    if (this.rm) return;
    this.flashColor = color;
    this.flashA = Math.max(this.flashA, strength);
  }

  ring(x, y, color, radius, dur) {
    this.addFx(dur, (g, p, cell) => {
      g.globalAlpha = (1 - p) * 0.9;
      g.strokeStyle = color;
      g.lineWidth = Math.max(1.5, cell * 0.08 * (1 - p));
      g.beginPath();
      g.ellipse(x * cell, y * cell, radius * ease.outCubic(p) * cell, radius * ease.outCubic(p) * cell * 0.75, 0, 0, Math.PI * 2);
      g.stroke();
      g.globalAlpha = 1;
    });
  }

  pillar(x, y, color) {
    this.addFx(900, (g, p, cell) => {
      const w = cell * 0.7 * (1 - p * 0.6);
      g.globalCompositeOperation = 'lighter';
      g.globalAlpha = ease.pulse(p) * 0.7;
      g.fillStyle = color;
      g.fillRect(x * cell - w / 2, 0, w, (y + 0.4) * cell);
      g.drawImage(glow(color), x * cell - cell, y * cell - cell * 0.8, cell * 2, cell * 1.6);
      g.globalCompositeOperation = 'source-over';
      g.globalAlpha = 1;
    });
  }

  /** Proyectil con estela: el golpe a distancia (La Escucha, El Campanero) o el virote del Ballestero. */
  bolt(from, to, dur, core, trail, fizzle, arrow = false) {
    const fx = from.x + 0.5;
    const fy = from.y + 0.45;
    const tx = to.x + 0.5;
    const ty = to.y + 0.45;
    this.addFx(dur, (g, p, cell) => {
      const k = arrow ? p : ease.inOutQuad(p);
      const x = fx + (tx - fx) * k;
      const y = fy + (ty - fy) * k - (arrow ? 0 : Math.sin(Math.PI * p) * 0.12);
      if (arrow) {
        const ang = Math.atan2(ty - fy, tx - fx);
        g.save();
        g.translate(x * cell, y * cell);
        g.rotate(ang);
        g.fillStyle = trail;
        g.fillRect(-cell * 0.35, -cell * 0.02, cell * 0.35, cell * 0.04);
        g.fillStyle = core;
        g.fillRect(0, -cell * 0.05, cell * 0.1, cell * 0.1);
        g.restore();
        return;
      }
      if (!this.rm) this.particles.spawn(x, y, (Math.random() - 0.5) * 0.4, (Math.random() - 0.5) * 0.4, 0.35, 0.09, trail, 0, 2, true);
      g.globalCompositeOperation = 'lighter';
      const r = cell * 0.42;
      g.drawImage(glow(trail), x * cell - r, y * cell - r, r * 2, r * 2);
      g.globalCompositeOperation = 'source-over';
      g.fillStyle = core;
      const s = cell * 0.14;
      g.fillRect(x * cell - s / 2, y * cell - s / 2, s, s);
      g.fillStyle = '#ffffff';
      g.fillRect(x * cell - s / 4, y * cell - s / 4, s / 2, s / 2);
    });
    if (fizzle && !arrow && this.timeline.running) {
      this.timeline.at(this.now - this.timeline.origin + dur, () => {
        this.particles.burst(tx, ty, this.rm ? 3 : 10, { speed: 1.6, life: 0.4, size: 0.05, colors: [core, trail], drag: 3, glow: true });
      });
    }
  }

  slash(x, y, facing, color = '#ffffff') {
    const base = { E: 0, S: Math.PI / 2, W: Math.PI, N: -Math.PI / 2 }[facing] ?? 0;
    this.addFx(240, (g, p, cell) => {
      const a0 = base - 1.1 + p * 1.2;
      g.globalAlpha = 1 - p;
      g.strokeStyle = '#ffffff';
      g.lineWidth = cell * 0.08 * (1 - p * 0.5);
      g.beginPath();
      g.arc((x - Math.cos(base) * 0.55) * cell, (y - Math.sin(base) * 0.55) * cell, cell * 0.55, a0, a0 + 1.1);
      g.stroke();
      g.strokeStyle = color;
      g.lineWidth = cell * 0.035;
      g.stroke();
      g.globalAlpha = 1;
    });
  }

  guardBubble(v, faint, color = '#8fd0ff') {
    this.addFx(faint ? 450 : 650, (g, p, cell) => {
      const [cx, cy] = this.center(v);
      const r = cell * (0.42 + ease.outCubic(p) * 0.12);
      g.globalAlpha = (1 - p) * (faint ? 0.45 : 0.85);
      g.strokeStyle = color;
      g.lineWidth = cell * 0.06;
      g.beginPath();
      g.arc(cx * cell, cy * cell, r, 0, Math.PI * 2);
      g.stroke();
      g.globalAlpha *= 0.25;
      g.fillStyle = color;
      g.fill();
      g.globalAlpha = 1;
    });
  }

  /** Muerte de enemigo: el sprite se deshace en sus propios píxeles. */
  dissolve(v) {
    const sp = sprite(v.d.kind);
    const [cx, cy] = this.center(v);
    v.dead = true;
    v.alpha = 0;
    v.alphaTarget = 0;
    v.leaving = true;
    this.ring(cx, cy, '#ffd08a', 0.8, 400);
    if (!sp) return;
    const scale = v.d.kind === 'CELADOR' ? 1.35 : 1;
    const unit = (0.9 * scale) / 18;
    const step = this.rm ? 3 : 1;
    const footY = v.y + 0.86;
    for (let i = 0; i < sp.pixels.length; i += step) {
      const [px, py, color] = sp.pixels[i];
      const x = v.x + 0.5 + (px - 9) * unit;
      const y = footY - (18 - py) * unit;
      const ax = x - cx;
      const ay = y - cy;
      this.particles.spawn(x, y, ax * 2.5 + (Math.random() - 0.5) * 0.6, ay * 1.5 - 0.6 - Math.random() * 0.9, 0.6 + Math.random() * 0.7, unit * 1.1, color, -0.4, 1.2);
    }
    this.particles.burst(cx, cy, this.rm ? 4 : 14, { speed: 1.4, life: 1, size: 0.05, colors: EMBER_COLORS, drag: 1.5, glow: true, up: 0.8 });
  }

  // ───────────── actualización por cuadro ─────────────

  update(dt) {
    const rm = this.rm;
    this.particles.update(dt);
    this.floaters.update(dt);
    this.trauma = Math.max(0, this.trauma - dt * 1.7);
    this.flashA = Math.max(0, this.flashA - dt * 3.2);
    this.fade = Math.max(0, this.fade - dt * 1.4);
    this.teleAlpha = Math.min(1, this.teleAlpha + dt * 3);

    for (const [id, v] of this.ents) {
      if (!v.locked) {
        const k = Math.min(1, dt * 12);
        v.x += (v.tx - v.x) * k;
        v.y += (v.ty - v.y) * k;
      }
      const ka = Math.min(1, dt * (rm ? 10 : 6));
      v.alpha += (v.alphaTarget - v.alpha) * ka;
      v.downT += (v.downTarget - v.downT) * Math.min(1, dt * 7);
      v.flash = Math.max(0, v.flash - dt * 5);
      v.jolt = Math.max(0, v.jolt - dt);
      if (v.trail > v.hp) v.trail = Math.max(v.hp, v.trail - dt * Math.max(1.5, v.maxHp * 0.7)); else v.trail = v.hp;
      if (v.leaving && !v.locked && v.alpha < 0.02) this.ents.delete(id);
      // La Brasa suelta briznas azules; el Renegado, brasas rojas que suben.
      if (v.type === 'hero' && v.alpha > 0.3) {
        if (isSpirit(v.d) && Math.random() < dt * (rm ? 1.5 : 7)) {
          this.particles.spawn(v.x + 0.3 + Math.random() * 0.4, v.y + 0.25 + Math.random() * 0.35, (Math.random() - 0.5) * 0.2, -0.5 - Math.random() * 0.5,
            0.8 + Math.random() * 0.5, 0.045, SPIRIT_COLORS[Math.floor(Math.random() * 3)], 0, 0.6, true);
        } else if (isRenegade(v.d) && Math.random() < dt * (rm ? 1 : 5)) {
          this.particles.spawn(v.x + 0.25 + Math.random() * 0.5, v.y + 0.75, (Math.random() - 0.5) * 0.25, -0.4 - Math.random() * 0.5,
            0.9 + Math.random() * 0.5, 0.045, BLOOD_COLORS[Math.floor(Math.random() * 3)], 0, 0.5, Math.random() < 0.6);
        }
      }
    }

    // Visión (color) y casillas conocidas (niebla), con transición suave. Los Faros del grupo también dan visión.
    const meV = this.meEnt();
    const mx = meV ? Math.round(meV.x) : this.state.me?.position?.x;
    const my = meV ? Math.round(meV.y) : this.state.me?.position?.y;
    const radius = this.shout ? SIZE * 2 : visionRadius(this.state.me);
    // Hilván ve con los dos cuerpos.
    const meB = this.state.me ? this.ents.get(secondId(this.state.me.playerId)) : null;
    const bx = meB ? Math.round(meB.x) : undefined;
    const by = meB ? Math.round(meB.y) : undefined;
    const beacons = isRenegade(this.state.me) ? [] : this.beacons;
    const kv = Math.min(1, dt * 5);
    for (let y = 0; y < SIZE; y++) {
      for (let x = 0; x < SIZE; x++) {
        const i = y * SIZE + x;
        const isKnown = this.tiles.has(tileKey(x, y));
        const lit = beacons.some((b) => Math.abs(x - b.x) + Math.abs(y - b.y) <= BEACON_RADIUS);
        const seen = (mx !== undefined && Math.abs(x - mx) + Math.abs(y - my) <= radius) || (bx !== undefined && Math.abs(x - bx) + Math.abs(y - by) <= radius);
        const target = isKnown && (seen || lit) ? 1 : 0;
        this.vision[i] += (target - this.vision[i]) * kv;
        const kt = isKnown ? 1 : 0;
        if (this.known[i] !== kt) {
          const next = kt ? Math.min(1, this.known[i] + dt * 2.2) : 0;
          if (next !== this.known[i]) { this.known[i] = next; this.fogDirty = true; }
        }
      }
    }

    // Ambiente: brasas que flotan, y chispas de braseros y fuegos.
    this.emberClock += dt * (rm ? 2 : 7);
    while (this.emberClock > 1) {
      this.emberClock -= 1;
      this.particles.spawn(Math.random() * SIZE, SIZE + 0.2, (Math.random() - 0.5) * 0.3, -0.35 - Math.random() * 0.45, 7 + Math.random() * 6, 0.035 + Math.random() * 0.03, EMBER_COLORS[Math.floor(Math.random() * 3)], -0.02, 0.05, true);
    }
    for (const lt of this.lightTiles) {
      if (Math.random() < dt * (rm ? 0.8 : 3)) {
        this.particles.spawn(lt.x + 0.35 + Math.random() * 0.3, lt.y + 0.35, (Math.random() - 0.5) * 0.3, -0.6 - Math.random() * 0.6, 1 + Math.random(), 0.04, EMBER_COLORS[Math.floor(Math.random() * 3)], 0, 0.3, true);
      }
    }
  }

  // ───────────── dibujo ─────────────

  rebuildLayer() {
    const W = this.W;
    const cell = this.cell;
    if (this.layer.width !== W) { this.layer.width = W; this.layer.height = W; }
    const g = this.layer.getContext('2d');
    g.clearRect(0, 0, W, W);
    const isWall = (x, y) => !inBounds(x, y) || this.tiles.get(tileKey(x, y)) === 'WALL';
    for (const [k, kind] of this.tiles) {
      const [x, y] = k.split(',').map(Number);
      paintTile(g, kind, x, y, cell, isWall);
    }
    grayCopy(this.layer, this.grayLayer);
    this.layerDirty = false;
  }

  render() {
    const g = this.ctx;
    const { W, cell } = this;
    if (this.layerDirty) this.rebuildLayer();
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.globalAlpha = 1;
    g.globalCompositeOperation = 'source-over';
    g.fillStyle = '#050307';
    g.fillRect(0, 0, W, W);
    if (this.trauma > 0) {
      const s = this.trauma * this.trauma * cell * 0.14;
      g.translate(Math.sin(this.t * 61) * s, Math.cos(this.t * 53) * s);
    }
    g.imageSmoothingEnabled = false;
    this.drawTiles(g, cell);
    this.drawObjects(g, cell);
    this.drawLighting(g, cell);
    this.drawFog(g);
    this.drawBlindness(g, cell);
    this.drawFlood(g, cell);
    this.drawBeacons(g, cell);
    drawNoise(this, g, cell);
    drawGroundEmbers(this, g, cell);
    drawHiddenTelegraphs(this, g, cell);
    this.drawTelegraphs(g, cell);
    drawEchoes(this, g, cell);
    drawRewind(this, g, cell, (this.state.armedMode ?? this.state.mode) === 'ABILITY');
    this.drawPreview(g, cell);
    this.drawStepGhost(g, cell);
    this.drawOrder(g, cell);
    drawThreads(this, g, cell);
    this.drawEntities(g, cell);
    drawLastWord(this, g, cell);
    drawSignalMark(this, g, cell);
    this.drawEffects(g, cell);
    this.particles.draw(g, cell);
    this.floaters.draw(g, cell);
    g.setTransform(1, 0, 0, 1, 0, 0);
    this.drawOverlay(g, cell);
    this.drawCaption(g, cell);
  }

  drawTiles(g, cell) {
    const paint = (x, y, ox, oy) => {
      const vis = this.vision[y * SIZE + x];
      const sx = x * cell;
      const sy = y * cell;
      const dx = (x + ox) * cell;
      const dy = (y + oy) * cell;
      if (vis < 0.99) g.drawImage(this.grayLayer, sx, sy, cell, cell, dx, dy, cell, cell);
      if (vis > 0.01) {
        g.globalAlpha = vis;
        g.drawImage(this.layer, sx, sy, cell, cell, dx, dy, cell, cell);
        g.globalAlpha = 1;
      }
    };
    for (const k of this.tiles.keys()) {
      const [x, y] = k.split(',').map(Number);
      if (!onShiftLine(this.shiftAnim, x, y)) paint(x, y, 0, 0);
    }
    this.drawShiftingLine(g, cell, paint);
  }

  /** La línea del Corrimiento: cada casilla se dibuja donde va ahora, y una copia entra por el borde contrario. */
  drawShiftingLine(g, cell, paint) {
    const s = this.shiftAnim;
    if (!s) return;
    g.save();
    g.beginPath();
    if (s.axis === 'row') g.rect(0, s.index * cell, this.W, cell); else g.rect(s.index * cell, 0, cell, this.W);
    g.clip();
    for (let i = 0; i < SIZE; i++) {
      const x = s.axis === 'row' ? i : s.index;
      const y = s.axis === 'row' ? s.index : i;
      if (!this.tiles.has(tileKey(x, y))) continue;
      const [ox, oy] = this.shiftOffset(x, y);
      paint(x, y, ox, oy);
      paint(x, y, ox + (s.axis === 'row' ? s.sign * SIZE : 0), oy + (s.axis === 'col' ? s.sign * SIZE : 0));
    }
    g.restore();
  }

  drawObjects(g, cell) {
    const t = this.t;
    const paint = (tx, ty, ox, oy) => {
      const k = tileKey(tx, ty);
      const kind = this.tileShown.get(k) ?? this.tiles.get(k);
      const x = tx + ox;
      const y = ty + oy;
      const vis = this.vision[ty * SIZE + tx];
      const lit = 0.35 + vis * 0.65;
      if (OBJECTS.has(kind)) {
        const sp = sprite(kind);
        const ox = x * cell - cell / 16;
        const oy = y * cell - cell / 16;
        const size = (cell * 18) / 16;
        if (vis < 0.99) g.drawImage(sp.gray, ox, oy, size, size);
        if (vis > 0.01) { g.globalAlpha = vis; g.drawImage(sp.base, ox, oy, size, size); g.globalAlpha = 1; }
      }
      if (kind === 'BRAZIER') this.drawFlames(g, x, y, cell, 3, 0.62, 5.5, lit);
      else if (kind === 'FIRE') this.drawFlames(g, x, y, cell, 4, 0.92, 7, lit);
      else if (kind === 'SPIKES_ARMED') this.drawSpikes(g, x, y, cell, 1, lit);
      else if (kind === 'STAIRS' || kind === 'ALTAR') {
        const color = kind === 'STAIRS' ? '#6b8fd6' : '#6ae0e0';
        g.globalCompositeOperation = 'lighter';
        g.globalAlpha = (0.18 + (this.rm ? 0 : 0.1 * Math.sin(t * 2 + x))) * lit;
        g.drawImage(glow(color), x * cell - cell * 0.1, y * cell - cell * 0.1, cell * 1.2, cell * 1.2);
        g.globalCompositeOperation = 'source-over';
        g.globalAlpha = 1;
      }
    };
    for (const k of this.tiles.keys()) {
      const [x, y] = k.split(',').map(Number);
      if (!onShiftLine(this.shiftAnim, x, y)) paint(x, y, 0, 0);
    }
    this.drawShiftingLine(g, cell, paint);
  }

  /** Llamas pixeladas: lenguas que suben y bajan a distinto ritmo. */
  drawFlames(g, x, y, cell, tongues, baseY, speed, lit) {
    const p = cell / 16;
    const t = this.t * (this.rm ? speed * 0.4 : speed);
    const seed = (x * 7 + y * 13) % 10;
    const spread = tongues === 3 ? 6 : 11;
    const base = y * cell + baseY * cell;
    g.globalAlpha = lit;
    for (let i = 0; i < tongues; i++) {
      const cx = x * cell + (8 - spread / 2 + (spread * (i + 0.5)) / tongues) * p;
      const h = (tongues === 3 ? 6 : 8) * p * (0.65 + 0.35 * Math.sin(t + i * 1.9 + seed) + 0.12 * Math.sin(t * 2.3 + i));
      const w = (tongues === 3 ? 2.4 : 3) * p;
      g.fillStyle = '#c8321a';
      g.fillRect(cx - w / 2, base - h, w, h);
      g.fillStyle = '#ff8a2a';
      g.fillRect(cx - w * 0.33, base - h * 0.72, w * 0.66, h * 0.72);
      g.fillStyle = '#ffe07a';
      g.fillRect(cx - w * 0.16, base - h * 0.38, w * 0.33, h * 0.38);
    }
    g.globalAlpha = 1;
  }

  drawSpikes(g, x, y, cell, rise, lit) {
    if (rise <= 0) return;
    const p = cell / 16;
    g.globalAlpha = lit;
    for (const [hx, hy] of [[5, 5], [11, 5], [5, 11], [11, 11], [8, 8]]) {
      const bx = x * cell + hx * p;
      const by = y * cell + (hy + 1) * p;
      const h = p * 4.5 * rise;
      g.fillStyle = '#c8ccd4';
      g.beginPath();
      g.moveTo(bx - p * 1.2, by);
      g.lineTo(bx, by - h);
      g.lineTo(bx + p * 1.2, by);
      g.closePath();
      g.fill();
      g.fillStyle = '#7a808a';
      g.fillRect(bx, by - h * 0.6, p * 0.8, h * 0.6);
    }
    if (!this.rm) {
      const glint = (this.t * 0.6 + x * 0.13 + y * 0.07) % 1;
      if (glint < 0.15) {
        g.globalCompositeOperation = 'lighter';
        g.globalAlpha = lit * (1 - glint / 0.15) * 0.8;
        g.drawImage(glow('#ffffff'), x * cell + cell * (0.2 + glint * 3), y * cell + cell * 0.2, cell * 0.3, cell * 0.3);
        g.globalCompositeOperation = 'source-over';
      }
    }
    g.globalAlpha = 1;
  }

  /** Oscuridad con la antorcha del héroe propio y la luz de braseros y fuegos. */
  drawLighting(g, cell) {
    const l = this.lightCtx;
    const W = this.W;
    const flick = this.rm ? 1 : 1 + 0.035 * Math.sin(this.t * 9.1) + 0.025 * Math.sin(this.t * 23.7);
    l.globalCompositeOperation = 'source-over';
    l.clearRect(0, 0, W, W);
    l.fillStyle = 'rgba(6,3,10,0.5)';
    l.fillRect(0, 0, W, W);
    l.globalCompositeOperation = 'destination-out';
    const white = glow('#ffffff', 0.62);
    const meV = this.meEnt();
    const meB = this.state.me ? this.ents.get(secondId(this.state.me.playerId)) : null;
    const torchRadius = this.shout ? SIZE * 1.5 : visionRadius(this.state.me) + 1.7;
    for (const body of [meV, meB]) {
      if (!body) continue;
      const r = torchRadius * cell * flick;
      const [cx, cy] = this.center(body);
      l.drawImage(white, cx * cell - r, cy * cell - r, r * 2, r * 2);
    }
    for (const lt of this.lightTiles) {
      const r = cell * 1.5 * flick;
      l.globalAlpha = 0.85;
      l.drawImage(white, (lt.x + 0.5) * cell - r, (lt.y + 0.5) * cell - r, r * 2, r * 2);
    }
    for (const v of this.ents.values()) {
      if (v.type !== 'hero' || v.isMe || v.alpha < 0.1) continue;
      if (isBlind(this.state.me) && !this.shout) continue; // a oscuras: los aliados no dan luz
      const r = cell * (isSpirit(v.d) ? 1.4 : 1.1);
      l.globalAlpha = 0.6 * v.alpha;
      l.drawImage(white, (v.x + 0.5) * cell - r, (v.y + 0.5) * cell - r, r * 2, r * 2);
    }
    const beacons = isRenegade(this.state.me) ? [] : this.beacons;
    for (const b of beacons) {
      const r = (BEACON_RADIUS + 1.2) * cell * flick;
      l.globalAlpha = 0.9;
      l.drawImage(white, (b.x + 0.5) * cell - r, (b.y + 0.5) * cell - r, r * 2, r * 2);
    }
    l.globalAlpha = 1;
    g.drawImage(this.light, 0, 0);

    // Calor: un toque aditivo anaranjado (azul para la Brasa, rojo para el Renegado).
    g.globalCompositeOperation = 'lighter';
    const warm = glow('#ff8a3a');
    if (meV) {
      const r = (visionRadius(this.state.me) + 0.5) * cell * flick;
      const [cx, cy] = this.center(meV);
      g.globalAlpha = 0.13;
      g.drawImage(isSpirit(this.state.me) ? glow('#4aa8ff') : isRenegade(this.state.me) ? glow('#ff3a2a') : warm, cx * cell - r, cy * cell - r, r * 2, r * 2);
    }
    for (const v of this.ents.values()) {
      if (v.type !== 'hero' || v.alpha < 0.1 || !(isSpirit(v.d) || isRenegade(v.d))) continue;
      const r = cell * (isSpirit(v.d) ? 1.5 : 1.2) * flick;
      const [cx, cy] = this.center(v);
      g.globalAlpha = (isSpirit(v.d) ? 0.28 : 0.22) * v.alpha;
      g.drawImage(glow(isSpirit(v.d) ? '#4aa8ff' : '#ff2a1a'), cx * cell - r, cy * cell - r, r * 2, r * 2);
    }
    for (const b of beacons) {
      const r = (BEACON_RADIUS + 0.8) * cell * flick;
      g.globalAlpha = 0.16;
      g.drawImage(glow('#ffe9a0'), (b.x + 0.5) * cell - r, (b.y + 0.5) * cell - r, r * 2, r * 2);
    }
    for (const lt of this.lightTiles) {
      const r = cell * 1.3 * flick;
      g.globalAlpha = 0.2 * (0.35 + 0.65 * this.vision[lt.y * SIZE + lt.x]);
      g.drawImage(warm, (lt.x + 0.5) * cell - r, (lt.y + 0.5) * cell - r, r * 2, r * 2);
    }
    g.globalAlpha = 1;
    g.globalCompositeOperation = 'source-over';
  }

  /** La Escucha conoce el terreno, pero lo que no está a su alcance queda a oscuras (el Grito lo enciende). */
  drawBlindness(g, cell) {
    if (!isBlind(this.state.me)) return;
    g.fillStyle = '#04030c';
    for (let y = 0; y < SIZE; y++) {
      for (let x = 0; x < SIZE; x++) {
        const dark = 1 - this.vision[y * SIZE + x];
        if (dark < 0.02) continue;
        g.globalAlpha = dark * 0.62;
        g.fillRect(x * cell, y * cell, cell, cell);
      }
    }
    g.globalAlpha = 1;
  }

  /** Niebla: máscara de 4 px por casilla escalada con suavizado, para bordes difuminados. */
  drawFog(g) {
    if (this.fogDirty) {
      const d = this.fogImg.data;
      const n = SIZE * 4;
      for (let py = 0; py < n; py++) {
        for (let px = 0; px < n; px++) {
          const i = (py * n + px) * 4;
          d[i] = 5; d[i + 1] = 3; d[i + 2] = 7;
          d[i + 3] = Math.round((1 - this.known[(py >> 2) * SIZE + (px >> 2)]) * 255);
        }
      }
      this.fogCtx.putImageData(this.fogImg, 0, 0);
      this.fogDirty = false;
    }
    g.imageSmoothingEnabled = true;
    g.drawImage(this.fog, 0, 0, this.W, this.W);
    g.imageSmoothingEnabled = false;
  }

  /** Zona del Faro: casillas a distancia ≤2 con un brillo dorado y un borde que respira. */
  drawBeacons(g, cell) {
    if (!this.beacons.length || isRenegade(this.state.me)) return;
    const pulse = this.rm ? 0.5 : 0.5 + 0.5 * Math.sin(this.t * 2.6);
    for (const b of this.beacons) {
      const appear = Math.min(1, (this.now - b.start) / 400);
      for (let dy = -BEACON_RADIUS; dy <= BEACON_RADIUS; dy++) {
        for (let dx = -BEACON_RADIUS; dx <= BEACON_RADIUS; dx++) {
          const d = Math.abs(dx) + Math.abs(dy);
          const x = b.x + dx;
          const y = b.y + dy;
          if (d > BEACON_RADIUS || !inBounds(x, y)) continue;
          g.globalAlpha = appear * (0.07 + pulse * 0.05) * (1 - d * 0.18);
          g.fillStyle = '#ffe9a0';
          g.fillRect(x * cell, y * cell, cell, cell);
          if (d === BEACON_RADIUS) {
            g.globalAlpha = appear * (0.3 + pulse * 0.25);
            g.strokeStyle = '#ffe9a0';
            g.lineWidth = Math.max(1, cell * 0.025);
            g.setLineDash([cell * 0.1, cell * 0.08]);
            g.lineDashOffset = this.rm ? 0 : -this.t * cell * 0.2;
            g.strokeRect(x * cell + cell * 0.08, y * cell + cell * 0.08, cell * 0.84, cell * 0.84);
            g.setLineDash(NO_DASH);
          }
        }
      }
    }
    g.globalAlpha = 1;
  }

  drawTelegraphs(g, cell) {
    if (!this.teleKeys.length) return;
    const rm = this.rm;
    const pulse = rm ? 0.5 : 0.5 + 0.5 * Math.sin(this.t * 5.5);
    const icon = dangerIcon(cell * 0.3);
    const meV = this.state.me?.hostEnemyId == null ? this.meEnt() : null; // a Polizón no lo atacan dentro de su huésped
    const meKey = meV ? tileKey(Math.round(meV.x), Math.round(meV.y)) : null;
    g.globalAlpha = this.teleAlpha;
    for (const p of this.teleKeys) {
      const x = p.x * cell;
      const y = p.y * cell;
      g.fillStyle = `rgba(226,48,36,${0.24 + pulse * 0.16})`;
      g.fillRect(x, y, cell, cell);
      g.strokeStyle = '#ff5a48';
      g.lineWidth = Math.max(1.5, cell * 0.045);
      const inset = cell * 0.06;
      g.strokeRect(x + inset, y + inset, cell - inset * 2, cell - inset * 2);
      g.drawImage(icon, x + cell - icon.width - cell * 0.06, y + cell * 0.06 - (rm ? 0 : pulse * cell * 0.03));
    }
    // El héroe propio está en una casilla amenazada: aviso más fuerte.
    if (meKey && this.teleSet.has(meKey) && meV) {
      const [cx, cy] = this.center(meV);
      g.strokeStyle = '#ff3a2a';
      g.lineWidth = cell * 0.06;
      g.globalAlpha = this.teleAlpha * (0.55 + pulse * 0.45);
      g.beginPath();
      g.arc(cx * cell, cy * cell, cell * (0.46 + pulse * 0.04), 0, Math.PI * 2);
      g.stroke();
    }
    g.globalAlpha = 1;
  }

  drawPreview(g, cell) {
    const color = MODE_COLORS[this.previewMode] ?? '#ffffff';
    const tilesFill = (list, alpha, dashed) => {
      if (!list.length) return;
      g.fillStyle = color;
      g.strokeStyle = color;
      g.lineWidth = Math.max(1.5, cell * 0.035);
      g.setLineDash(dashed ? DASH.map((d) => d * cell / 60) : NO_DASH);
      g.lineDashOffset = this.rm ? 0 : -this.t * cell * 0.5;
      for (const p of list) {
        g.globalAlpha = alpha;
        g.fillRect(p.x * cell + 2, p.y * cell + 2, cell - 4, cell - 4);
        g.globalAlpha = Math.min(1, alpha * 3);
        g.strokeRect(p.x * cell + cell * 0.08, p.y * cell + cell * 0.08, cell * 0.84, cell * 0.84);
      }
      g.setLineDash(NO_DASH);
      g.globalAlpha = 1;
    };
    tilesFill(this.previewFaint, 0.1, true);
    tilesFill(this.preview, 0.26, true);
    const s = this.state;
    // Corrimiento: flechas a lo largo de la línea y adónde iría la escalera.
    if (this.previewMode === 'ABILITY' && s.me?.hero === 'MAMPOSTERO' && !isSpirit(s.me) && !s.pendingOrder && s.me.position && !this.timeline.running) {
      if (this.hoverDir) drawShiftPreview(this, g, cell, shiftFromOrder(s.me.position, this.hoverDir), 1);
      else if (s.armedMode) for (const d of DIR_NAMES) drawShiftPreview(this, g, cell, shiftFromOrder(s.me.position, d), 0.4);
    }
    // Modo armado: marco dorado alrededor del héroe propio.
    const meV = this.meEnt();
    if (meV && s.armedMode && !s.pendingOrder) {
      const [cx, cy] = this.center(meV);
      g.strokeStyle = MODE_COLORS[s.armedMode] ?? '#e8c15a';
      g.lineWidth = cell * 0.04;
      g.setLineDash(DASH.map((d) => d * cell / 60));
      g.lineDashOffset = this.rm ? 0 : -this.t * cell * 0.4;
      g.strokeRect((cx - 0.46) * cell, (cy - 0.46) * cell, cell * 0.92, cell * 0.92);
      g.setLineDash(NO_DASH);
    }
  }

  /** Orden ya enviada: flecha animada (o escudo girando si es Guardar) y sus casillas. */
  drawOrder(g, cell) {
    const s = this.state;
    const order = s.pendingOrder;
    if (!order || !s.me || this.orderHidden) return;
    const meV = this.meEnt();
    if (!meV) return;
    const [cx, cy] = this.center(meV);
    const color = MODE_COLORS[order.type] ?? '#ffffff';
    const pulse = this.rm ? 0.5 : 0.5 + 0.5 * Math.sin(this.t * 6);
    if (order.type === 'GUARD' && isSpirit(s.me)) {
      // Faro pendiente: la zona que se iluminará.
      g.fillStyle = '#ffe9a0';
      g.globalAlpha = 0.08 + pulse * 0.05;
      for (const p of this.affected('GUARD', null, [])) g.fillRect(p.x * cell + 2, p.y * cell + 2, cell - 4, cell - 4);
      g.globalAlpha = 1;
    }
    if (order.type === 'GUARD') {
      g.strokeStyle = isSpirit(s.me) ? '#ffe9a0' : color;
      g.lineWidth = cell * 0.05;
      g.setLineDash([cell * 0.16, cell * 0.1]);
      g.lineDashOffset = this.rm ? 0 : -this.t * cell * 0.3;
      g.beginPath();
      g.arc(cx * cell, cy * cell, cell * 0.47, 0, Math.PI * 2);
      g.stroke();
      g.setLineDash(NO_DASH);
      return;
    }
    const v = DIRS[order.direction];
    if (!v) return;
    // Paso ya enviado: se ve el camino y la flecha de la acción sale de donde quedaría.
    let [ax, ay] = [cx, cy];
    if (order.step && order.type !== 'MOVE') {
      const landed = this.landing(order.step);
      if (landed) {
        const stepV = DIRS[order.step];
        this.drawOrderArrow(g, cell, cx, cy, stepV, MODE_COLORS.MOVE, pulse);
        ax = landed.pos.x + 0.5;
        ay = landed.pos.y + 0.5;
      }
    }
    const tiles = this.affected(order.type, order.direction, []);
    g.fillStyle = color;
    g.globalAlpha = 0.12 + pulse * 0.06;
    for (const p of tiles) g.fillRect(p.x * cell + 2, p.y * cell + 2, cell - 4, cell - 4);
    g.globalAlpha = 1;
    if (order.type === 'ABILITY' && s.me.hero === 'MAMPOSTERO' && !isSpirit(s.me) && s.me.position) drawShiftPreview(this, g, cell, shiftFromOrder(s.me.position, order.direction), 0.8);
    this.drawOrderArrow(g, cell, ax, ay, v, color, pulse);
    // Hilván: la misma orden, en espejo, en el otro cuerpo.
    const meB = this.ents.get(secondId(s.me.playerId));
    if (meB && order.type !== 'ABILITY') {
      const [bx, by] = this.center(meB);
      this.drawOrderArrow(g, cell, bx, by, DIRS[mirrored(order.direction)], color, pulse);
    }
  }

  drawOrderArrow(g, cell, cx, cy, v, color, pulse) {
    const len = 0.62 + pulse * 0.08;
    const sx = (cx + v[0] * 0.3) * cell;
    const sy = (cy + v[1] * 0.3) * cell;
    const tx = (cx + v[0] * (0.3 + len)) * cell;
    const ty = (cy + v[1] * (0.3 + len)) * cell;
    g.strokeStyle = '#120806';
    g.lineWidth = cell * 0.12;
    g.lineCap = 'round';
    g.beginPath(); g.moveTo(sx, sy); g.lineTo(tx, ty); g.stroke();
    g.strokeStyle = color;
    g.lineWidth = cell * 0.07;
    g.setLineDash([cell * 0.12, cell * 0.08]);
    g.lineDashOffset = this.rm ? 0 : -this.t * cell * 0.6;
    g.beginPath(); g.moveTo(sx, sy); g.lineTo(tx, ty); g.stroke();
    g.setLineDash(NO_DASH);
    g.lineCap = 'butt';
    const h = cell * 0.2;
    g.fillStyle = color;
    g.strokeStyle = '#120806';
    g.lineWidth = cell * 0.03;
    g.beginPath();
    g.moveTo(tx + v[0] * h, ty + v[1] * h);
    g.lineTo(tx - v[0] * h * 0.2 + v[1] * h, ty - v[1] * h * 0.2 + v[0] * h);
    g.lineTo(tx - v[0] * h * 0.2 - v[1] * h, ty - v[1] * h * 0.2 - v[0] * h);
    g.closePath();
    g.fill();
    g.stroke();
  }

  drawEntities(g, cell) {
    const list = this.order;
    list.length = 0;
    for (const v of this.ents.values()) if (v.alpha > 0.01) list.push(v);
    // La Brasa flota por encima de quien comparta su casilla.
    list.sort((a, b) => a.y + (isSpirit(a.d) ? 0.5 : 0) - b.y - (isSpirit(b.d) ? 0.5 : 0) || (a.isMe ? 1 : 0) - (b.isMe ? 1 : 0));
    for (const v of list) {
      if (v.type === 'hero' && isSpirit(v.d)) this.drawSpirit(g, cell, v);
      else this.drawEntity(g, cell, v);
    }
  }

  /** Brasa errante: llama azul translúcida que flota y titila, sin sombra sólida ni barra de vida. */
  drawSpirit(g, cell, v) {
    const rm = this.rm;
    const t = this.t;
    const d = v.d;
    const alpha = v.alpha;
    const cx = (v.x + 0.5 + v.ox) * cell;
    const bob = rm ? 0 : Math.sin(t * 2.3 + v.seed) * 0.05;
    const lift = (0.2 + bob + v.hop) * cell;
    const feet = (v.y + 0.5 + v.oy) * cell + cell * 0.36;
    const flick = rm ? 1 : 0.85 + 0.15 * Math.sin(t * 11 + v.seed) + 0.06 * Math.sin(t * 27);

    // Reflejo azul en el suelo en lugar de sombra.
    g.globalAlpha = alpha * 0.3;
    g.fillStyle = '#2a6fd0';
    g.beginPath();
    g.ellipse(cx, feet, cell * 0.26, cell * 0.08, 0, 0, Math.PI * 2);
    g.fill();
    g.globalAlpha = alpha * (v.isMe ? 0.75 : 0.5);
    g.strokeStyle = v.isMe ? '#ffd36a' : '#6fc8ff';
    g.lineWidth = Math.max(1.5, cell * (v.isMe ? 0.04 : 0.025));
    g.setLineDash([cell * 0.08, cell * 0.06]);
    g.lineDashOffset = rm ? 0 : -t * cell * 0.15;
    g.beginPath();
    g.ellipse(cx, feet, cell * 0.34, cell * 0.12, 0, 0, Math.PI * 2);
    g.stroke();
    g.setLineDash(NO_DASH);

    // Halo, llama (tres cuadros) y un segundo pase aditivo que la hace brillar.
    const cy = feet - lift - cell * 0.32;
    g.globalCompositeOperation = 'lighter';
    g.globalAlpha = alpha * 0.55 * flick;
    const halo = cell * 0.8;
    g.drawImage(glow('#3f9ae8'), cx - halo, cy - halo, halo * 2, halo * 2);
    g.globalCompositeOperation = 'source-over';
    const frame = rm ? 0 : Math.floor(t * 7 + v.seed) % 3;
    const sp = sprite(`EMBER_SPIRIT_${frame}`);
    if (sp) {
      const scale = this.spriteScale;
      const size = 18 * scale;
      const stretch = rm ? 1 : 1 + 0.05 * Math.sin(t * 9 + v.seed);
      g.save();
      g.translate(Math.round(cx), Math.round(feet - lift));
      g.scale(1 / stretch, stretch);
      g.globalAlpha = alpha * 0.78;
      g.drawImage(sp.base, -size / 2, -size + scale, size, size);
      g.globalCompositeOperation = 'lighter';
      g.globalAlpha = alpha * 0.3 * flick;
      g.drawImage(sp.base, -size / 2, -size + scale, size, size);
      if (v.flash > 0) { g.globalAlpha = alpha * Math.min(1, v.flash); g.drawImage(sp.white, -size / 2, -size + scale, size, size); }
      g.restore();
      g.globalCompositeOperation = 'source-over';
    }
    g.globalAlpha = 1;
    if (alpha < 0.15) return;

    // Quién es: marca dorada si es la propia; si no, la inicial en un círculo azul.
    const headY = feet - lift - cell * 0.9;
    if (v.isMe) {
      const s = cell * 0.09;
      const b = rm ? 0 : Math.sin(t * 4) * cell * 0.03;
      g.globalAlpha = alpha;
      g.fillStyle = '#ffd36a';
      g.strokeStyle = '#120806';
      g.lineWidth = Math.max(1, cell * 0.02);
      g.beginPath();
      g.moveTo(cx - s, headY - s * 1.6 + b);
      g.lineTo(cx + s, headY - s * 1.6 + b);
      g.lineTo(cx, headY + b);
      g.closePath();
      g.fill();
      g.stroke();
    } else if (d.displayName) {
      this.drawBadge(g, cell, cx - cell * 0.36, feet - cell * 0.7, d.displayName.slice(0, 1).toUpperCase(), '#6fc8ff', alpha);
    }
    g.globalAlpha = 1;
  }

  /** Insignia redonda con una letra o un símbolo en la esquina de la casilla. */
  drawBadge(g, cell, bx, by, text, color, alpha, ink = '#120806') {
    const r = cell * 0.12;
    g.globalAlpha = alpha * 0.95;
    g.fillStyle = '#120806';
    g.beginPath();
    g.arc(bx, by, r + Math.max(1, cell * 0.02), 0, Math.PI * 2);
    g.fill();
    g.fillStyle = color;
    g.beginPath();
    g.arc(bx, by, r, 0, Math.PI * 2);
    g.fill();
    g.font = `900 ${Math.round(cell * 0.16)}px system-ui, sans-serif`;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillStyle = ink;
    g.fillText(text, bx, by + cell * 0.01);
    g.globalAlpha = 1;
  }

  drawEntity(g, cell, v) {
    const rm = this.rm;
    const t = this.t;
    const d = v.d;
    const kind = v.type === 'hero' ? d.hero : d.kind;
    const renegade = v.type === 'hero' && isRenegade(d);
    const sp = sprite(kind);
    const boss = kind === 'CELADOR';
    const alpha = v.alpha;
    const hosting = v.type === 'enemy' && this.state.me?.hostEnemyId === d.enemyId;
    const jx = v.jolt > 0 ? Math.sin(t * 80) * v.jolt * 0.12 : 0;
    const cx = (v.x + 0.5 + v.ox + jx) * cell;
    const cy = (v.y + 0.5 + v.oy) * cell;
    const feet = cy + cell * 0.36;
    const flying = kind === 'POLILLA';
    const hover = flying ? 0.16 + (rm ? 0 : 0.05 * Math.sin(t * 5 + v.seed)) : 0;
    const lift = (v.hop + hover) * cell;

    // Sombra y anillo de identidad.
    g.globalAlpha = alpha * 0.45 * (1 - Math.min(0.6, (v.hop + hover) * 0.8));
    g.fillStyle = '#000000';
    g.beginPath();
    g.ellipse(cx, feet, cell * (boss ? 0.42 : 0.3), cell * 0.1, 0, 0, Math.PI * 2);
    g.fill();
    if (renegade && v.downT < 0.5) {
      // Renegado: aura roja que late y anillo de enemigo (también para sí mismo).
      const pulse = rm ? 0.5 : 0.5 + 0.5 * Math.sin(t * 3.4 + v.seed);
      g.globalCompositeOperation = 'lighter';
      g.globalAlpha = alpha * (0.3 + pulse * 0.25);
      g.drawImage(glow('#ff2a1a'), cx - cell * 0.75, cy - cell * 0.85, cell * 1.5, cell * 1.5);
      g.globalCompositeOperation = 'source-over';
      g.globalAlpha = alpha * (0.6 + pulse * 0.35);
      g.strokeStyle = '#ff4a3a';
      g.lineWidth = Math.max(1.5, cell * 0.045);
      g.beginPath();
      g.ellipse(cx, feet, cell * (0.36 + pulse * 0.03), cell * 0.13, 0, 0, Math.PI * 2);
      g.stroke();
      if (v.isMe) {
        g.globalAlpha = alpha * 0.8;
        g.strokeStyle = '#ffd36a';
        g.lineWidth = Math.max(1, cell * 0.02);
        g.beginPath();
        g.ellipse(cx, feet, cell * 0.42, cell * 0.16, 0, 0, Math.PI * 2);
        g.stroke();
      }
    } else if (v.type === 'hero' && v.downT < 0.5) {
      const color = v.isMe ? '#ffd36a' : HERO_COLORS[kind] ?? '#cccccc';
      const pulse = v.isMe && !rm ? 0.5 + 0.5 * Math.sin(t * 3.2) : 0.5;
      g.globalAlpha = alpha * (v.isMe ? 0.55 + pulse * 0.4 : 0.55);
      g.strokeStyle = color;
      g.lineWidth = Math.max(1.5, cell * (v.isMe ? 0.045 : 0.03));
      g.beginPath();
      g.ellipse(cx, feet, cell * (0.36 + (v.isMe ? pulse * 0.03 : 0)), cell * 0.13, 0, 0, Math.PI * 2);
      g.stroke();
    } else if (v.type === 'enemy') {
      g.globalAlpha = alpha * 0.5;
      g.strokeStyle = '#c8321a';
      g.lineWidth = Math.max(1, cell * 0.025);
      g.beginPath();
      g.ellipse(cx, feet, cell * (boss ? 0.44 : 0.32), cell * 0.11, 0, 0, Math.PI * 2);
      g.stroke();
    }
    if (hosting) drawHostMark(this, g, cell, v, cx, cy, feet, alpha);
    if (boss && d.phase === 2) {
      g.globalCompositeOperation = 'lighter';
      g.globalAlpha = alpha * (0.35 + (rm ? 0 : 0.2 * Math.sin(t * 7)));
      g.drawImage(glow('#ff2a1a'), cx - cell * 0.8, cy - cell * 0.9, cell * 1.6, cell * 1.6);
      g.globalCompositeOperation = 'source-over';
    }
    g.globalAlpha = 1;

    // Sprite con respiración, mirada (volteo horizontal) y estados.
    if (sp) {
      const scale = this.spriteScale * (boss ? 1.35 : 1);
      const size = 18 * scale;
      const breath = rm ? 0 : Math.sin(t * (v.type === 'hero' ? 2.4 : 3) + v.seed);
      const sy = 1 + breath * 0.035;
      const sx = 1 - breath * 0.02;
      const flip = v.facing === 'W' ? -1 : 1;
      g.save();
      g.translate(Math.round(cx), Math.round(feet - lift));
      if (v.downT > 0.01) g.rotate(-v.downT * Math.PI / 2 * flip);
      g.scale(sx * flip, sy);
      const ox = -size / 2;
      const oy = -size + scale;
      const down = v.downT;
      g.globalAlpha = alpha * (1 - down);
      if (down < 0.99) g.drawImage(sp.base, ox, oy, size, size);
      if (renegade && down < 0.99) {
        g.globalAlpha = alpha * (1 - down) * (rm ? 0.45 : 0.38 + 0.14 * Math.sin(t * 3.4 + v.seed));
        g.drawImage(sp.blood, ox, oy, size, size);
      }
      if (down > 0.01) { g.globalAlpha = alpha * down; g.drawImage(sp.gray, ox, oy, size, size); }
      if (v.frozen) { g.globalAlpha = alpha * 0.6; g.drawImage(sp.ice, ox, oy, size, size); }
      if (v.flash > 0) { g.globalAlpha = alpha * Math.min(1, v.flash); g.drawImage(sp.white, ox, oy, size, size); }
      g.restore();
    } else {
      g.globalAlpha = alpha;
      g.fillStyle = v.type === 'hero' ? '#cccccc' : '#7a2b2b';
      g.fillRect(cx - cell * 0.3, cy - cell * 0.3, cell * 0.6, cell * 0.6);
    }
    g.globalAlpha = 1;
    if (alpha < 0.15) return;

    // Mirada: un chevrón en el borde de la casilla.
    const dir = DIRS[v.facing];
    if (dir && v.downT < 0.5) {
      const color = v.type === 'enemy' || (renegade && !v.isMe) ? '#ff8a7a' : v.isMe ? '#ffffff' : HERO_COLORS[kind] ?? '#dddddd';
      const fx = cx + dir[0] * cell * 0.47;
      const fy = cy + dir[1] * cell * 0.47;
      const s = cell * 0.085;
      g.globalAlpha = alpha * 0.9;
      g.fillStyle = color;
      g.strokeStyle = '#120806';
      g.lineWidth = Math.max(1, cell * 0.02);
      g.beginPath();
      g.moveTo(fx + dir[0] * s, fy + dir[1] * s);
      g.lineTo(fx - dir[0] * s + dir[1] * s, fy - dir[1] * s + dir[0] * s);
      g.lineTo(fx - dir[0] * s - dir[1] * s, fy - dir[1] * s - dir[0] * s);
      g.closePath();
      g.fill();
      g.stroke();
      g.globalAlpha = 1;
    }

    // Una sola barra aunque Hilván tenga dos cuerpos; el Avaro no tiene: sus ascuas son su vida.
    if (v.type === 'hero' && d.hero === 'AVARO') this.drawEmberLife(g, cell, v, cx, feet, alpha);
    else if (!d.secondary) this.drawHp(g, cell, v, cx, feet, boss, alpha);
    // El Celador descansa una ronda entre golpes: no es un fallo, no anuncia ni se mueve.
    if (boss && !d.telegraph?.length && !this.timeline.running) this.drawBadge(g, cell, cx + cell * 0.46, cy - cell * 0.5, '⏸', '#c8c0b0', alpha);
    // La Usurera ve en cada aliado lo que le debe; nadie más.
    if (d.debtToYou && !d.secondary) this.drawBadge(g, cell, cx + cell * 0.36, cy - cell * 0.34, String(d.debtToYou), '#e8c44a', alpha);

    // Quién es quién: marca dorada sobre el propio; nombre sobre los aliados. El Renegado lleva además una
    // calavera roja de enemigo (la ve también él mismo).
    const headY = feet - lift - (boss ? 1.25 : 0.86) * cell;
    if (renegade && v.downT < 0.5) {
      this.drawBadge(g, cell, cx + cell * 0.36, cy - cell * 0.34, '☠', '#ff4a3a', alpha, '#1a0302');
      if (!v.isMe) {
        this.drawBadge(g, cell, cx - cell * 0.36, cy - cell * 0.34, (d.displayName ?? '?').slice(0, 1).toUpperCase(), '#ff8a7a', alpha);
        return this.drawDownMark(g, cell, v, cx, cy, alpha);
      }
    }
    if (v.isMe) {
      if (v.downT >= 0.5) return this.drawDownMark(g, cell, v, cx, cy, alpha);
      const bob = rm ? 0 : Math.sin(t * 4) * cell * 0.03;
      const s = cell * 0.09;
      g.globalAlpha = alpha;
      g.fillStyle = '#ffd36a';
      g.strokeStyle = '#120806';
      g.lineWidth = Math.max(1, cell * 0.02);
      g.beginPath();
      g.moveTo(cx - s, headY - s * 1.6 + bob);
      g.lineTo(cx + s, headY - s * 1.6 + bob);
      g.lineTo(cx, headY + bob);
      g.closePath();
      g.fill();
      g.stroke();
      g.globalAlpha = 1;
    } else if (v.type === 'hero' && d.displayName) {
      // Aliado: insignia con su inicial en la esquina de su casilla (no invade la de arriba).
      const r = cell * 0.12;
      const bx = cx - cell * 0.36;
      const by = cy - cell * 0.34;
      g.globalAlpha = alpha * 0.95;
      g.fillStyle = '#120806';
      g.beginPath();
      g.arc(bx, by, r + Math.max(1, cell * 0.02), 0, Math.PI * 2);
      g.fill();
      g.fillStyle = HERO_COLORS[kind] ?? '#dddddd';
      g.beginPath();
      g.arc(bx, by, r, 0, Math.PI * 2);
      g.fill();
      g.font = `900 ${Math.round(cell * 0.16)}px system-ui, sans-serif`;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillStyle = '#120806';
      g.fillText(d.displayName.slice(0, 1).toUpperCase(), bx, by + cell * 0.01);
      g.globalAlpha = 1;
    }
    this.drawDownMark(g, cell, v, cx, cy, alpha);
  }

  /** Héroe caído: cruz con las rondas que le quedan para ser levantado. */
  drawDownMark(g, cell, v, cx, cy, alpha) {
    const d = v.d;
    const rm = this.rm;
    if (v.type === 'hero' && d.state === 'DOWN' && v.downT > 0.5) {
      g.font = `900 ${Math.round(cell * 0.24)}px system-ui, sans-serif`;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillStyle = '#e2675a';
      g.globalAlpha = alpha * (rm ? 1 : 0.7 + 0.3 * Math.sin(this.t * 4));
      const text = d.downRoundsLeft !== null && d.downRoundsLeft !== undefined ? `✝ ${d.downRoundsLeft}` : '✝';
      g.lineWidth = Math.max(2, cell * 0.05);
      g.strokeStyle = '#120806';
      g.strokeText(text, cx, cy - cell * 0.28);
      g.fillText(text, cx, cy - cell * 0.28);
      g.globalAlpha = 1;
    }
  }

  /** Avaro: en lugar de barra, las ascuas que le quedan (son su vida). */
  drawEmberLife(g, cell, v, cx, feet, alpha) {
    if (v.d.state === 'EXTINCT' || isSpirit(v.d)) return;
    const text = `✦${v.d.embers ?? 0}`;
    g.globalAlpha = alpha;
    g.font = `900 ${Math.round(cell * 0.22)}px system-ui, sans-serif`;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.lineWidth = Math.max(2, cell * 0.05);
    g.strokeStyle = '#120806';
    const y = feet + cell * 0.1;
    g.strokeText(text, cx, y);
    g.fillStyle = (v.d.embers ?? 0) <= 1 ? '#ff8a6a' : '#ffd36a';
    g.fillText(text, cx, y);
    g.globalAlpha = 1;
  }

  /** Barra de vida con segmentos y un rastro claro que se encoge tras un golpe. */
  drawHp(g, cell, v, cx, feet, boss, alpha) {
    if (!v.maxHp) return;
    const w = cell * (boss ? 1.1 : 0.72);
    const h = Math.max(3, cell * 0.075);
    const x0 = cx - w / 2;
    const y0 = feet + cell * 0.07;
    const ratio = clamp01(v.hp / v.maxHp);
    const trail = clamp01(v.trail / v.maxHp);
    g.globalAlpha = alpha;
    g.fillStyle = '#120806';
    g.fillRect(x0 - 1, y0 - 1, w + 2, h + 2);
    g.fillStyle = '#3a1612';
    g.fillRect(x0, y0, w, h);
    if (trail > ratio) { g.fillStyle = '#fff3d0'; g.fillRect(x0, y0, w * trail, h); }
    const hostile = v.type === 'enemy' || (isRenegade(v.d) && !v.isMe);
    g.fillStyle = hostile ? '#e2574a' : ratio > 0.5 ? '#6cd38a' : ratio > 0.25 ? '#e8c15a' : '#e2675a';
    g.fillRect(x0, y0, w * ratio, h);
    g.fillStyle = 'rgba(255,255,255,0.25)';
    g.fillRect(x0, y0, w * ratio, Math.max(1, h * 0.3));
    if (v.maxHp <= 12) {
      g.fillStyle = '#120806';
      for (let i = 1; i < v.maxHp; i++) g.fillRect(Math.round(x0 + (w * i) / v.maxHp), y0, 1, h);
    }
    const shield = v.isMe ? v.d.shield : 0;
    if (shield > 0) {
      g.fillStyle = '#8fd0ff';
      for (let i = 0; i < Math.min(shield, 6); i++) g.fillRect(x0 + w + 2 + i * (h + 1), y0, h, h);
    }
    g.globalAlpha = 1;
  }

  drawEffects(g, cell) {
    const fx = this.effects;
    let i = 0;
    while (i < fx.length) {
      const e = fx[i];
      const p = (this.now - e.start) / e.dur;
      if (p >= 1) { fx[i] = fx[fx.length - 1]; fx.pop(); continue; }
      if (p >= 0) e.draw(g, p, cell);
      i++;
    }
    g.globalAlpha = 1;
    g.globalCompositeOperation = 'source-over';
  }

  drawOverlay(g, cell) {
    const W = this.W;
    if (!this.vignette) {
      this.vignette = makeCanvas(W, W);
      const v = this.vignette.getContext('2d');
      const grad = v.createRadialGradient(W / 2, W / 2, W * 0.36, W / 2, W / 2, W * 0.74);
      grad.addColorStop(0, 'rgba(0,0,0,0)');
      grad.addColorStop(1, 'rgba(0,0,0,0.45)');
      v.fillStyle = grad;
      v.fillRect(0, 0, W, W);
    }
    g.drawImage(this.vignette, 0, 0);
    // Repique sonando: el borde late hasta que vence el plazo de los demás.
    const bell = this.state.bell;
    if (bell && !bell.closed) {
      const k = this.rm ? 0.5 : 0.5 + 0.5 * Math.sin(this.t * 8);
      g.globalAlpha = 0.45 + k * 0.45;
      g.strokeStyle = '#ffd98a';
      g.lineWidth = cell * 0.09;
      g.strokeRect(0, 0, W, W);
      g.globalAlpha = 1;
    }
    if (this.flashA > 0.01) {
      g.globalAlpha = this.flashA * 0.3;
      g.fillStyle = this.flashColor;
      g.fillRect(0, 0, W, W);
    }
    if (this.fade > 0.01) {
      g.globalAlpha = Math.min(1, this.fade * 1.2);
      g.fillStyle = '#000000';
      g.fillRect(0, 0, W, W);
    }
    g.globalAlpha = 1;
    const b = this.banner;
    if (b) {
      const p = (this.now - b.start) / b.dur;
      if (p >= 1) { this.banner = null; return; }
      const a = p < 0.15 ? p / 0.15 : p > 0.75 ? (1 - p) / 0.25 : 1;
      const y = W * 0.42 - (this.rm ? 0 : (1 - ease.outCubic(Math.min(1, p / 0.15))) * cell * 0.3);
      g.globalAlpha = a * 0.6;
      g.fillStyle = '#000000';
      g.fillRect(0, y - cell * 0.7, W, cell * 1.35);
      g.globalAlpha = a;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.lineJoin = 'round';
      g.font = `900 ${Math.round(cell * 0.62)}px system-ui, sans-serif`;
      g.lineWidth = cell * 0.1;
      g.strokeStyle = '#120806';
      g.strokeText(b.text, W / 2, y - cell * 0.12);
      g.fillStyle = b.color ?? '#ffd36a';
      g.fillText(b.text, W / 2, y - cell * 0.12);
      if (b.sub) {
        g.font = `600 ${Math.round(cell * 0.24)}px system-ui, sans-serif`;
        g.fillStyle = '#e8dccb';
        g.fillText(b.sub, W / 2, y + cell * 0.4);
      }
      g.globalAlpha = 1;
    }
  }
}
