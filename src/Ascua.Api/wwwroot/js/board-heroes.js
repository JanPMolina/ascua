// Lo propio de los nueve héroes en el tablero (planteamiento/05-heroes.md): geometría de las vistas previas,
// capas que se dibujan sobre el suelo (anuncios sin autor, Ecos, Volver, ruido, ascuas en el suelo, hilo de Hilván,
// órdenes de Último en hablar, marcas de señales) y los efectos de sus eventos. board.js llama a estas funciones.
//
// Todo se calcula con lo que trae la vista del jugador; las vistas previas son una aproximación visual: el servidor decide.
import { heroInfo } from './heroes.js';
import { glow, sprite, HERO_COLORS } from './sprites.js';
import { ease } from './fx.js';
import { SIGNALS } from './labels.js';
// Lado de la sala (9 u 11): enlace vivo con board.js, que lo cambia al llegar la vista.
import { SIZE } from './board.js';

const DIRS = { N: [0, -1], S: [0, 1], E: [1, 0], W: [-1, 0] };
const COINS = ['#ffe07a', '#e8c44a', '#ff9a3a'];
const ECHO_COLORS = ['#6fd8c8', '#c8fff4', '#2f7f76'];
const PROOF_COLOR = '#c58bff';
const NO_DASH = [];

const inBounds = (x, y) => x >= 0 && y >= 0 && x < SIZE && y < SIZE;
const key = (x, y) => `${x},${y}`;
const manhattan = (a, b) => Math.abs(a.x - b.x) + Math.abs(a.y - b.y);

/** Este y oeste van al revés en el segundo cuerpo de Hilván. */
export const mirrored = (dir) => ({ E: 'W', W: 'E' }[dir] ?? dir);

/** La Escucha es ciega, salvo como Brasa. */
export const isBlind = (me) => me?.hero === 'ESCUCHA' && me?.state !== 'EMBER_SPIRIT';

// ───────────── geometría ─────────────

/** Casillas entre los dos cuerpos si Tensar haría algo: misma fila o columna, a ≥2 y sin muro entre ellos. */
export function tensarCells(a, b, tiles) {
  if (!a || !b || (a.x !== b.x && a.y !== b.y) || manhattan(a, b) < 2) return [];
  const dx = Math.sign(b.x - a.x);
  const dy = Math.sign(b.y - a.y);
  const cells = [];
  for (let p = { x: a.x + dx, y: a.y + dy }; p.x !== b.x || p.y !== b.y; p = { x: p.x + dx, y: p.y + dy }) {
    if (tiles.get(key(p.x, p.y)) === 'WALL') return [];
    cells.push(p);
  }
  return cells;
}

/**
 * Cómo quedarían los anuncios con Corregir hacia `dir`. Cada enemigo con alguna casilla a ≤2 se desplaza entero; la casilla
 * que cae en un muro o fuera se pierde. De los anuncios sin autor solo se conocen las casillas: se agrupan por vecindad.
 */
export function correctedTiles(origin, dir, enemies, hidden, tiles, out) {
  const [dx, dy] = DIRS[dir] ?? [0, 0];
  const groups = [...(enemies?.values() ?? [])].map((e) => e.telegraph ?? []).filter((t) => t.length);
  const rest = [...(hidden ?? [])];
  while (rest.length) {
    const cluster = [rest.pop()];
    for (let i = 0; i < cluster.length; i++) {
      for (let j = rest.length - 1; j >= 0; j--) {
        if (manhattan(cluster[i], rest[j]) === 1) cluster.push(rest.splice(j, 1)[0]);
      }
    }
    groups.push(cluster);
  }
  for (const tele of groups) {
    if (!tele.some((p) => manhattan(p, origin) <= 2)) continue;
    for (const p of tele) {
      const x = p.x + dx;
      const y = p.y + dy;
      if (inBounds(x, y) && tiles.get(key(x, y)) !== 'WALL') out.push({ x, y });
    }
  }
  return out;
}

/** Corrimiento: { axis: 'row'|'col', index, sign } a partir de ROOM_SHIFTED o de la orden del Mampostero. */
export function shiftFromEvent(ev) {
  const detail = String(ev?.detail ?? '');
  const axis = detail.startsWith('ROW') ? 'row' : detail.startsWith('COLUMN') ? 'col' : null;
  if (!axis || ev.amount === undefined || ev.amount === null) return null;
  return { axis, index: ev.amount, sign: detail.endsWith('_E') || detail.endsWith('_S') ? 1 : -1 };
}

export function shiftFromOrder(origin, dir) {
  if (!DIRS[dir]) return null;
  const horizontal = dir === 'E' || dir === 'W';
  return { axis: horizontal ? 'row' : 'col', index: horizontal ? origin.y : origin.x, sign: dir === 'E' || dir === 'S' ? 1 : -1 };
}

export const onShiftLine = (s, x, y) => Boolean(s) && (s.axis === 'row' ? y === s.index : x === s.index);

/** Dónde queda una casilla de la línea tras correrse (lo que sale por un borde entra por el otro). */
export function shiftedPos(s, p) {
  if (!onShiftLine(s, p.x, p.y)) return p;
  const wrap = (v) => (v + s.sign + SIZE) % SIZE;
  return s.axis === 'row' ? { x: wrap(p.x), y: p.y } : { x: p.x, y: wrap(p.y) };
}

/** Texto para el jugador: «la fila 3 se corre al este». */
export function shiftText(s) {
  const dir = s.axis === 'row' ? (s.sign > 0 ? 'este' : 'oeste') : (s.sign > 0 ? 'sur' : 'norte');
  return `${s.axis === 'row' ? 'La fila' : 'La columna'} ${s.index} se corre al ${dir}`;
}

// ───────────── capas sobre el suelo ─────────────

/** Anuncios sin autor: morados y rayados, para que no se confundan con los rojos de un enemigo que se ve. */
export function drawHiddenTelegraphs(b, g, cell) {
  const list = b.overlay.hidden;
  if (!list.length) return;
  const pulse = b.rm ? 0.5 : 0.5 + 0.5 * Math.sin(b.t * 4.5);
  g.save();
  for (const p of list) {
    const x = p.x * cell;
    const y = p.y * cell;
    g.globalAlpha = b.teleAlpha * (0.16 + pulse * 0.12);
    g.fillStyle = PROOF_COLOR;
    g.fillRect(x, y, cell, cell);
    g.globalAlpha = b.teleAlpha * 0.55;
    g.strokeStyle = PROOF_COLOR;
    g.lineWidth = Math.max(1, cell * 0.025);
    g.beginPath();
    for (let i = 1; i < 4; i++) {
      g.moveTo(x + (cell * i) / 4, y + cell * 0.06);
      g.lineTo(x + cell * 0.06, y + (cell * i) / 4);
      g.moveTo(x + cell * 0.94, y + (cell * i) / 4);
      g.lineTo(x + (cell * i) / 4, y + cell * 0.94);
    }
    g.stroke();
    g.globalAlpha = b.teleAlpha;
    g.lineWidth = Math.max(1.5, cell * 0.04);
    g.setLineDash([cell * 0.12, cell * 0.08]);
    g.strokeRect(x + cell * 0.06, y + cell * 0.06, cell * 0.88, cell * 0.88);
    g.setLineDash(NO_DASH);
    g.font = `900 ${Math.round(cell * 0.28)}px system-ui, sans-serif`;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.lineWidth = Math.max(2, cell * 0.04);
    g.strokeStyle = '#120806';
    g.strokeText('?', x + cell / 2, y + cell / 2);
    g.fillStyle = '#ecd8ff';
    g.fillText('?', x + cell / 2, y + cell / 2);
  }
  g.restore();
}

/** Ondas de lo que oyó La Escucha en la última resolución. */
export function drawNoise(b, g, cell) {
  if (!b.overlay.noise.length) return;
  const t = b.rm ? 0.35 : b.t;
  g.save();
  g.strokeStyle = '#c0e0ff';
  for (const p of b.overlay.noise) {
    const cx = (p.x + 0.5) * cell;
    const cy = (p.y + 0.5) * cell;
    g.globalAlpha = 0.14;
    g.fillStyle = '#c0e0ff';
    g.fillRect(p.x * cell, p.y * cell, cell, cell);
    for (let i = 0; i < 3; i++) {
      const k = b.rm ? (i + 1) / 3 : (t * 0.8 + i / 3 + (p.x + p.y) * 0.13) % 1;
      g.globalAlpha = (1 - k) * 0.85;
      g.lineWidth = Math.max(1, cell * 0.03);
      g.beginPath();
      g.arc(cx, cy, cell * (0.12 + k * 0.42), 0, Math.PI * 2);
      g.stroke();
    }
  }
  g.restore();
}

/** Silueta de un sprite como luz fría; `tag` es lo que se escribe al lado (rondas o vida). */
function drawGhost(b, g, cell, kind, x, y, alpha, tag) {
  const sp = sprite(kind);
  const cx = (x + 0.5) * cell;
  const feet = (y + 0.5) * cell + cell * 0.36;
  if (sp) {
    const size = 18 * b.spriteScale;
    g.globalAlpha = alpha;
    g.drawImage(sp.ghost, Math.round(cx - size / 2), Math.round(feet - size + b.spriteScale), size, size);
  }
  if (tag) {
    g.globalAlpha = Math.min(1, alpha + 0.35);
    b.drawBadge(g, cell, cx + cell * 0.34, feet - cell * 0.72, tag, '#6fd8c8', Math.min(1, alpha + 0.35));
  }
  g.globalAlpha = 1;
}

/** Ecos programados: silueta, adónde apuntan, a qué casillas llegan y en cuántas rondas disparan. */
export function drawEchoes(b, g, cell) {
  const { echoes } = b.overlay;
  if (!echoes.length) return;
  const pulse = b.rm ? 0.5 : 0.5 + 0.5 * Math.sin(b.t * 3.2);
  const round = b.state.round ?? 0;
  for (const e of echoes) {
    const hero = b.heroOf(e.ownerId);
    const [dx, dy] = DIRS[e.direction] ?? [0, 0];
    const range = heroInfo(hero).range ?? 1;
    g.fillStyle = '#6fd8c8';
    g.strokeStyle = '#6fd8c8';
    g.lineWidth = Math.max(1, cell * 0.03);
    for (let i = 1; i <= range; i++) {
      const x = e.position.x + dx * i;
      const y = e.position.y + dy * i;
      if (!inBounds(x, y) || b.tiles.get(key(x, y)) === 'WALL') break;
      g.globalAlpha = 0.14 + pulse * 0.1;
      g.fillRect(x * cell + 2, y * cell + 2, cell - 4, cell - 4);
      g.globalAlpha = 0.75;
      g.setLineDash([cell * 0.07, cell * 0.06]);
      g.strokeRect(x * cell + cell * 0.08, y * cell + cell * 0.08, cell * 0.84, cell * 0.84);
      g.setLineDash(NO_DASH);
    }
    const left = Math.max(0, e.fireRound - round);
    drawGhost(b, g, cell, hero, e.position.x, e.position.y, 0.38 + pulse * 0.18, left > 0 ? String(left) : '!');
    const fx = (e.position.x + 0.5 + dx * 0.42) * cell;
    const fy = (e.position.y + 0.5 + dy * 0.42) * cell;
    const s = cell * 0.1;
    g.globalAlpha = 0.95;
    g.fillStyle = '#c8fff4';
    g.beginPath();
    g.moveTo(fx + dx * s, fy + dy * s);
    g.lineTo(fx - dx * s + dy * s, fy - dy * s + dx * s);
    g.lineTo(fx - dx * s - dy * s, fy - dy * s - dx * s);
    g.closePath();
    g.fill();
    g.globalAlpha = 1;
  }
}

/** Destino de Volver: dónde caería y con cuánta vida. Más fuerte con la habilidad elegida. */
export function drawRewind(b, g, cell, active) {
  const me = b.state.me;
  const tile = me?.rewindTile;
  if (!tile || me.state !== 'ALIVE' || me.hero !== 'ANTEAYER') return;
  const pulse = b.rm ? 0.5 : 0.5 + 0.5 * Math.sin(b.t * 2.4);
  const alpha = active ? 0.55 + pulse * 0.2 : 0.2 + pulse * 0.08;
  const x = tile.x * cell;
  const y = tile.y * cell;
  g.globalAlpha = active ? 0.9 : 0.5;
  g.strokeStyle = '#c8fff4';
  g.lineWidth = Math.max(1, cell * 0.035);
  g.setLineDash([cell * 0.1, cell * 0.07]);
  g.lineDashOffset = b.rm ? 0 : b.t * cell * 0.25;
  g.beginPath();
  g.arc(x + cell / 2, y + cell / 2, cell * 0.44, 0, Math.PI * 2);
  g.stroke();
  g.setLineDash(NO_DASH);
  drawGhost(b, g, cell, me.hero, tile.x, tile.y, alpha, null);
  const hp = `♥${me.rewindHp ?? '?'}`;
  g.globalAlpha = active ? 1 : 0.7;
  g.font = `800 ${Math.round(cell * 0.18)}px system-ui, sans-serif`;
  g.textAlign = 'center';
  g.textBaseline = 'middle';
  g.lineWidth = Math.max(2, cell * 0.04);
  g.strokeStyle = '#120806';
  g.strokeText(hp, x + cell / 2, y + cell * 0.14);
  g.fillStyle = '#ffb4a8';
  g.fillText(hp, x + cell / 2, y + cell * 0.14);
  g.globalAlpha = 1;
}

/** Ascuas en el suelo: un montón que brilla, con la cantidad si es más de una. */
export function drawGroundEmbers(b, g, cell) {
  const { ground } = b.overlay;
  if (!ground.length) return;
  for (const pile of ground) {
    const [ox, oy] = b.shiftOffset(pile.position.x, pile.position.y);
    const cx = (pile.position.x + 0.5 + ox) * cell;
    const cy = (pile.position.y + 0.62 + oy) * cell;
    const flick = b.rm ? 1 : 0.8 + 0.2 * Math.sin(b.t * 7 + pile.position.x * 3);
    g.globalCompositeOperation = 'lighter';
    g.globalAlpha = 0.55 * flick;
    const r = cell * 0.42;
    g.drawImage(glow('#ff8a3a'), cx - r, cy - r, r * 2, r * 2);
    g.globalCompositeOperation = 'source-over';
    g.globalAlpha = 1;
    const shown = Math.min(5, pile.amount);
    const s = cell * 0.075;
    for (let i = 0; i < shown; i++) {
      const a = (i / Math.max(1, shown)) * Math.PI * 2 + 0.6;
      const d = shown === 1 ? 0 : cell * 0.1;
      g.fillStyle = COINS[i % COINS.length];
      g.fillRect(Math.round(cx + Math.cos(a) * d - s / 2), Math.round(cy + Math.sin(a) * d * 0.6 - s / 2), s, s);
    }
    if (pile.amount > 1) {
      g.font = `800 ${Math.round(cell * 0.18)}px system-ui, sans-serif`;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.lineWidth = Math.max(2, cell * 0.04);
      g.strokeStyle = '#120806';
      g.strokeText(String(pile.amount), cx, cy - cell * 0.22);
      g.fillStyle = '#ffe07a';
      g.fillText(String(pile.amount), cx, cy - cell * 0.22);
    }
  }
  g.globalAlpha = 1;
}

/** Hilo de Hilván entre sus cuerpos; se tensa (y lo que hay en medio se marca) cuando Tensar haría algo. */
export function drawThreads(b, g, cell) {
  for (const v of b.ents.values()) {
    if (v.type !== 'hero' || !v.d.secondary || v.alpha < 0.05) continue;
    const main = b.ents.get(v.ownerId);
    if (!main || main.alpha < 0.05) continue;
    const color = HERO_COLORS.HILVAN;
    const cells = main.isMe ? tensarCells(main.d.position, v.d.position, b.tiles) : [];
    const taut = cells.length > 0;
    const pulse = b.rm ? 0.5 : 0.5 + 0.5 * Math.sin(b.t * 5);
    const ax = (main.x + 0.5) * cell;
    const ay = (main.y + 0.6) * cell;
    const bx = (v.x + 0.5) * cell;
    const by = (v.y + 0.6) * cell;
    if (taut && !b.timeline.running) {
      g.fillStyle = color;
      g.globalAlpha = 0.1 + pulse * 0.1;
      for (const p of cells) g.fillRect(p.x * cell + 2, p.y * cell + 2, cell - 4, cell - 4);
    }
    g.globalAlpha = Math.min(main.alpha, v.alpha) * (taut ? 0.95 : 0.45);
    g.strokeStyle = color;
    g.lineWidth = Math.max(1, cell * (taut ? 0.035 : 0.018));
    g.setLineDash(taut ? NO_DASH : [cell * 0.08, cell * 0.08]);
    g.beginPath();
    g.moveTo(ax, ay);
    if (taut) {
      g.lineTo(bx, by);
    } else {
      // Flojo: el hilo cuelga un poco.
      g.quadraticCurveTo((ax + bx) / 2, (ay + by) / 2 + cell * 0.35, bx, by);
    }
    g.stroke();
    g.setLineDash(NO_DASH);
    g.globalAlpha = 1;
  }
}

/** Huésped de Polizón: anillo verde y sus ojos asomando, solo en la vista de Polizón. */
export function drawHostMark(b, g, cell, v, cx, cy, feet, alpha) {
  const pulse = b.rm ? 0.5 : 0.5 + 0.5 * Math.sin(b.t * 3.6);
  g.globalAlpha = alpha * (0.65 + pulse * 0.35);
  g.strokeStyle = HERO_COLORS.POLIZON;
  g.lineWidth = Math.max(1.5, cell * 0.05);
  g.beginPath();
  g.ellipse(cx, feet, cell * (0.38 + pulse * 0.03), cell * 0.14, 0, 0, Math.PI * 2);
  g.stroke();
  g.globalCompositeOperation = 'lighter';
  g.globalAlpha = alpha * (0.2 + pulse * 0.15);
  g.drawImage(glow(HERO_COLORS.POLIZON), cx - cell * 0.7, cy - cell * 0.8, cell * 1.4, cell * 1.4);
  g.globalCompositeOperation = 'source-over';
  b.drawBadge(g, cell, cx + cell * 0.36, cy - cell * 0.34, '◉', HERO_COLORS.POLIZON, alpha);
  g.globalAlpha = 1;
}

const ORDER_ICONS = { MOVE: '➜', ATTACK: '⚔', ABILITY: '✺', INTERACT: '✋', GUARD: '⛨' };

/** Órdenes de Último en hablar sobre cada aliado que se ve. Con `typesOnly` no hay dirección. */
export function drawLastWord(b, g, cell) {
  const heard = b.state.lastWord;
  if (!heard?.orders?.length || b.timeline.running) return;
  const pulse = b.rm ? 0.5 : 0.5 + 0.5 * Math.sin(b.t * 4);
  for (const o of heard.orders) {
    const v = b.ents.get(o.playerId);
    if (!v || v.alpha < 0.3) continue;
    const [cx, cy] = b.center(v);
    const px = cx * cell;
    const py = cy * cell;
    const dir = o.direction ? DIRS[o.direction] : null;
    if (dir) {
      g.strokeStyle = '#7fb0e8';
      g.lineWidth = cell * 0.06;
      g.globalAlpha = 0.85;
      g.lineCap = 'round';
      g.beginPath();
      g.moveTo(px + dir[0] * cell * 0.25, py + dir[1] * cell * 0.25);
      g.lineTo(px + dir[0] * cell * (0.8 + pulse * 0.08), py + dir[1] * cell * (0.8 + pulse * 0.08));
      g.stroke();
      g.lineCap = 'butt';
      const tx = px + dir[0] * cell * (0.9 + pulse * 0.08);
      const ty = py + dir[1] * cell * (0.9 + pulse * 0.08);
      const s = cell * 0.13;
      g.fillStyle = '#d8ecff';
      g.beginPath();
      g.moveTo(tx + dir[0] * s, ty + dir[1] * s);
      g.lineTo(tx - dir[0] * s * 0.3 + dir[1] * s, ty - dir[1] * s * 0.3 + dir[0] * s);
      g.lineTo(tx - dir[0] * s * 0.3 - dir[1] * s, ty - dir[1] * s * 0.3 - dir[0] * s);
      g.closePath();
      g.fill();
    }
    g.globalAlpha = 1;
    b.drawBadge(g, cell, px, py - cell * 0.52, ORDER_ICONS[o.type] ?? '?', '#7fb0e8', 1);
  }
}

/** Casilla elegida para la próxima señal. */
export function drawSignalMark(b, g, cell) {
  const p = b.state.signalMark;
  if (!p) return;
  const k = b.rm ? 0.5 : 0.5 + 0.5 * Math.sin(b.t * 6);
  const x = p.x * cell;
  const y = p.y * cell;
  const s = cell * 0.26;
  g.strokeStyle = '#ffe9a0';
  g.lineWidth = Math.max(1.5, cell * 0.05);
  g.globalAlpha = 0.65 + k * 0.35;
  const i = cell * 0.05;
  const e = cell - i;
  g.beginPath();
  for (const [ax, ay, dx, dy] of [[i, i, 1, 1], [e, i, -1, 1], [i, e, 1, -1], [e, e, -1, -1]]) {
    g.moveTo(x + ax + dx * s, y + ay);
    g.lineTo(x + ax, y + ay);
    g.lineTo(x + ax, y + ay + dy * s);
  }
  g.stroke();
  g.globalAlpha = 1;
}

/** Corrimiento antes de darlo: flechas a lo largo de la línea, por dónde entra lo que sale, y adónde iría la escalera. */
export function drawShiftPreview(b, g, cell, s, alpha) {
  if (!s) return;
  const [dx, dy] = s.axis === 'row' ? [s.sign, 0] : [0, s.sign];
  const offset = b.rm ? 0.5 : (b.t * 0.9) % 1;
  g.fillStyle = '#ffe9a0';
  for (let i = 0; i < SIZE; i++) {
    const x = s.axis === 'row' ? i : s.index;
    const y = s.axis === 'row' ? s.index : i;
    const px = (x + 0.5 + dx * (offset - 0.5) * 0.5) * cell;
    const py = (y + 0.5 + dy * (offset - 0.5) * 0.5) * cell;
    const h = cell * 0.1;
    g.globalAlpha = alpha * 0.8;
    g.beginPath();
    g.moveTo(px + dx * h, py + dy * h);
    g.lineTo(px - dx * h + dy * h, py - dy * h + dx * h);
    g.lineTo(px - dx * h - dy * h, py - dy * h - dx * h);
    g.closePath();
    g.fill();
    if (b.tiles.get(key(x, y)) === 'STAIRS') {
      const to = shiftedPos(s, { x, y });
      g.globalAlpha = alpha;
      g.strokeStyle = '#8fb8ff';
      g.lineWidth = Math.max(1.5, cell * 0.05);
      g.setLineDash([cell * 0.12, cell * 0.08]);
      g.strokeRect(to.x * cell + cell * 0.1, to.y * cell + cell * 0.1, cell * 0.8, cell * 0.8);
      g.setLineDash(NO_DASH);
      g.font = `800 ${Math.round(cell * 0.15)}px system-ui, sans-serif`;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillStyle = '#d8e6ff';
      g.fillText('escalera', (to.x + 0.5) * cell, (to.y + 0.5) * cell);
      g.fillStyle = '#ffe9a0';
    }
  }
  // Borde de salida: lo que cruza recibe 2 de daño.
  const ex = s.axis === 'row' ? (s.sign > 0 ? SIZE - 1 : 0) : s.index;
  const ey = s.axis === 'row' ? s.index : (s.sign > 0 ? SIZE - 1 : 0);
  g.globalAlpha = alpha * 0.9;
  g.font = `900 ${Math.round(cell * 0.2)}px system-ui, sans-serif`;
  g.textAlign = 'center';
  g.textBaseline = 'middle';
  g.fillStyle = '#ff8a7a';
  g.fillText('−2', (ex + 0.5) * cell, (ey + 0.2) * cell);
  g.globalAlpha = 1;
}

// ───────────── efectos de los eventos ─────────────

/** Eventos de los nueve héroes; devuelve true si lo manejó. */
export function applyHeroEvent(b, ev, target, actor) {
  const rm = b.rm;
  const me = b.state.me;
  switch (ev.type) {
    case 'ECHO_FIRED': {
      const at = ev.position;
      if (!at) return true;
      const [dx, dy] = DIRS[ev.detail] ?? [0, 1];
      b.overlay.echoes = b.overlay.echoes.filter((e) => !(e.ownerId === ev.actorId && e.position.x === at.x && e.position.y === at.y));
      const hero = b.heroOf(ev.actorId);
      const sp = sprite(hero);
      b.addFx(420, (g, p, cell) => {
        const k = ease.pulse(p) * 0.36;
        if (sp) {
          const size = 18 * b.spriteScale;
          g.globalAlpha = (1 - p) * 0.8;
          g.drawImage(sp.ghost, (at.x + 0.5 + dx * k) * cell - size / 2, (at.y + 0.86 + dy * k) * cell - size + b.spriteScale, size, size);
          g.globalAlpha = 1;
        }
      });
      b.slash(at.x + dx + 0.5, at.y + dy + 0.5, ev.detail, '#6fd8c8');
      b.particles.burst(at.x + 0.5, at.y + 0.5, rm ? 4 : 14, { speed: 1.5, life: 0.6, size: 0.05, colors: ECHO_COLORS, drag: 3, glow: true });
      b.floaters.add('eco', at.x + 0.5, at.y + 0.05, { color: '#c8fff4', size: 0.2 });
      return true;
    }
    case 'ATTACK_CORRECTED': {
      const tiles = ev.tiles ?? [];
      b.addFx(620, (g, p, cell) => {
        g.globalAlpha = (1 - p) * 0.75;
        g.fillStyle = PROOF_COLOR;
        for (const q of tiles) g.fillRect(q.x * cell, q.y * cell, cell, cell);
        g.globalAlpha = 1;
      });
      const first = tiles[0];
      if (first) b.floaters.add('¡se desvió!', first.x + 0.5, first.y + 0.3, { color: '#ecd8ff', size: 0.22, life: 1.2 });
      b.shake(0.12);
      return true;
    }
    case 'BETRAYAL_RECORDED':
      if (target) {
        const [cx, cy] = b.center(target);
        b.floaters.add('nadie lo sabe', cx, cy - 0.55, { color: '#ecd8ff', size: 0.18, life: 1.5 });
      }
      return true;
    case 'HOST_BOARDED': {
      if (target) {
        const [cx, cy] = b.center(target);
        const from = actor ? b.center(actor) : [cx, cy];
        b.particles.stream(from[0], from[1], cx, cy, rm ? 6 : 20, { colors: ['#b8ff5a', '#a8d85a', '#5a7a3a'], life: 0.5, size: 0.06 });
        b.ring(cx, cy, HERO_COLORS.POLIZON, 0.7, 500);
        b.floaters.add('a bordo', cx, cy - 0.5, { color: '#d8ffa0', size: 0.22, delay: 0.2 });
      }
      return true;
    }
    case 'HOST_EXITED': {
      const at = ev.position;
      if (!at) return true;
      b.particles.burst(at.x + 0.5, at.y + 0.5, rm ? 5 : 18, { speed: 1.6, life: 0.6, size: 0.07, colors: ['#b8ff5a', '#6e5a34', '#a89058'], drag: 3 });
      b.floaters.add('¡reaparece!', at.x + 0.5, at.y + 0.1, { color: '#d8ffa0', size: 0.22 });
      return true;
    }
    case 'HOST_DRAINED':
      if (target) {
        const [cx, cy] = b.center(target);
        b.floaters.add('drenado', cx, cy - 0.5, { color: '#d8ffa0', size: 0.2 });
        b.particles.vortex(cx, cy, rm ? 4 : 12, { radius: 0.5, life: 0.5, size: 0.05, colors: ['#b8ff5a', '#5a7a3a'], inward: true });
      }
      return true;
    case 'WARDEN_TURNED':
      if (target) {
        if (ev.detail) target.facing = ev.detail;
        const [cx, cy] = b.center(target);
        b.ring(cx, cy, HERO_COLORS.POLIZON, 1, 500);
        b.floaters.add('¡gira!', cx, cy - 0.7, { color: '#d8ffa0', size: 0.26 });
        target.jolt = rm ? 0 : 0.3;
      }
      return true;
    case 'SHOUTED': {
      const [cx, cy] = actor ? b.center(actor) : [SIZE / 2, SIZE / 2];
      for (const [r, d] of [[3, 500], [6, 750], [9, 1000]]) b.ring(cx, cy, '#c0e0ff', r, d);
      b.floaters.add('¡GRITO!', cx, cy - 0.5, { color: '#dff0ff', size: 0.34, life: 1.4 });
      b.shake(0.3);
      b.screenFlash('#c0e0ff', 0.5);
      return true;
    }
    case 'DEBT_CHANGED':
      debtFx(b, ev, target, actor);
      return true;
    case 'EMBERS_DROPPED': {
      const at = ev.position;
      if (!at) return true;
      for (let i = 0; i < (rm ? 3 : 8) * Math.max(1, ev.amount ?? 1); i++) {
        b.particles.spawn(at.x + 0.5, at.y + 0.3, (Math.random() - 0.5) * 1.8, -1 - Math.random() * 1.4, 0.7, 0.05, COINS[i % 3], 4.5, 0.6, true);
      }
      b.floaters.add(`−${ev.amount ?? 1} ✦`, at.x + 0.5, at.y + 0.1, { color: '#ffb04a', size: 0.26 });
      return true;
    }
    case 'EMBERS_PICKED_UP': {
      const at = ev.position ?? (actor ? { x: actor.x, y: actor.y } : null);
      if (!at) return true;
      b.particles.burst(at.x + 0.5, at.y + 0.5, rm ? 4 : 14, { speed: 1.4, life: 0.6, size: 0.05, colors: COINS, drag: 2, glow: true, up: 0.8 });
      b.floaters.add(`+${ev.amount ?? 1} ✦`, at.x + 0.5, at.y + 0.1, { color: '#ffe07a', size: 0.28 });
      return true;
    }
    case 'EMBERS_LOST': {
      const who = actor;
      if (who && ev.detail === 'BRIBE') return true; // el soborno ya se ve con ENEMY_BRIBED
      if (who) {
        const [cx, cy] = b.center(who);
        b.floaters.add(`${ev.amount ?? -1} ✦`, cx, cy - 0.45, { color: '#ff9a7a', size: 0.24 });
      }
      return true;
    }
    case 'ENEMY_BRIBED': {
      if (target) {
        const [cx, cy] = b.center(target);
        const from = actor ? b.center(actor) : [cx, cy];
        b.particles.stream(from[0], from[1], cx, cy, rm ? 5 : 16, { colors: COINS, life: 0.45, size: 0.05 });
        b.floaters.add(`soborno −${ev.amount ?? 1} ✦`, cx, cy - 0.5, { color: '#ffe07a', size: 0.2, life: 1.3, delay: 0.2 });
      }
      return true;
    }
    case 'BELL_RUNG': {
      const [cx, cy] = actor ? b.center(actor) : [SIZE / 2, SIZE / 2];
      b.ring(cx, cy, '#ffe9a0', 3, 600);
      b.ring(cx, cy, '#d8a040', 5.5, 900);
      b.ring(cx, cy, '#d8a040', 8, 1200);
      b.shake(0.25);
      const mine = ev.actorId === me?.playerId;
      b.banner = {
        text: '¡Repique!',
        sub: mine ? 'En la próxima ronda los demás tienen solo 8 s' : 'En la próxima ronda tendrás solo 8 s',
        start: b.now,
        dur: 2200,
        color: '#ffd98a',
      };
      return true;
    }
    default:
      return false;
  }
}

const DEBT_TEXT = { LOAN: 'deuda', INTEREST: 'interés', PAID: 'cobro', EXTINCTION: 'cobro', MATCH_END: 'cobro', CANCELLED: 'perdonada' };

/** Préstamo, interés y cobro: solo les llega a la Usurera y al deudor. El cobro es un chorro de oro del deudor a ella. */
function debtFx(b, ev, target, actor) {
  const rm = b.rm;
  const collected = ['PAID', 'EXTINCTION', 'MATCH_END'].includes(ev.detail);
  const word = DEBT_TEXT[ev.detail] ?? 'deuda';
  const amount = Math.abs(ev.amount ?? 1);
  if (target) {
    const [cx, cy] = b.center(target);
    b.floaters.add(`${word} ${collected ? '−' : '+'}${amount}`, cx, cy - 0.55, { color: collected ? '#ffe07a' : '#ff9a7a', size: 0.22, life: 1.4 });
    if (collected && actor) {
      const [ax, ay] = b.center(actor);
      b.particles.stream(cx, cy, ax, ay, rm ? 6 : 22, { colors: COINS, life: 0.7, size: 0.06 });
    } else {
      b.ring(cx, cy, '#e8c44a', 0.7, 450);
    }
  }
}

/** Marca de una señal rápida: un pulso en la casilla, o la frase sobre quien la envió si no marcó ninguna. */
export function signalFx(b, signal) {
  const phrase = SIGNALS[signal.kind]?.text ?? signal.kind;
  const danger = signal.kind === 'DANGER' || signal.kind === 'HELP';
  const color = danger ? '#ff9a7a' : '#ffe9a0';
  if (signal.position) {
    const { x, y } = signal.position;
    b.ring(x + 0.5, y + 0.5, color, 1.2, 800);
    b.ring(x + 0.5, y + 0.5, color, 0.7, 600);
    b.floaters.add(phrase, x + 0.5, y + 0.15, { color, size: 0.24, life: 2.2 });
    b.addFx(2400, (g, p, cell) => {
      g.globalAlpha = (1 - p) * 0.9;
      g.strokeStyle = color;
      g.lineWidth = Math.max(1.5, cell * 0.05);
      g.strokeRect(x * cell + cell * 0.06, y * cell + cell * 0.06, cell * 0.88, cell * 0.88);
      g.globalAlpha = 1;
    });
    return;
  }
  const from = b.ents.get(signal.fromPlayerId);
  if (from) {
    const [cx, cy] = b.center(from);
    b.floaters.add(phrase, cx, cy - 0.6, { color, size: 0.24, life: 2 });
  }
}
