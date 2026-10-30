// Dibuja la vista del jugador en un <canvas> 9×9. Solo se dibuja lo que vino en la vista:
// una casilla que el servidor nunca envió queda negra (niebla); una recordada pero fuera de
// la visión actual se oscurece.

export const SIZE = 9;

const TILE_COLORS = {
  FLOOR: '#3b342d',
  WALL: '#57504a',
  SPIKES: '#5e4a5e',
  SPIKES_ARMED: '#8a5a8a',
  BRAZIER: '#7a4a24',
  CRACK: '#4a3e33',
  CHEST: '#c9952e',
  CHEST_OPENED: '#6b5a3a',
  ALTAR: '#5aa0a0',
  STAIRS: '#6b8fd6',
  FIRE: '#e05a1e',
};

const TILE_GLYPHS = { CHEST: '▣', CHEST_OPENED: '□', ALTAR: '✦', STAIRS: '≡', BRAZIER: '♨', CRACK: '⌇', SPIKES: '⋀', SPIKES_ARMED: '▲', FIRE: '✸' };

const HERO_COLORS = { YUNQUE: '#8fa3b8', CHISPA: '#f5a65b', CUERVO: '#9b7fd1', RAIZ: '#6cc28a' };
const HERO_LETTERS = { YUNQUE: 'Y', CHISPA: 'C', CUERVO: 'K', RAIZ: 'R' };
const ENEMY_LETTERS = { CARRONERO: 'c', POLILLA: 'p', GELATINA: 'g', SALTASAPO: 's', BALLESTERO: 'b', TOTEM: 't', CELADOR: 'Ω' };

const DIR_VECTORS = { N: [0, -1], S: [0, 1], E: [1, 0], W: [-1, 0] };

/** Radio de visión (Manhattan): 3, +1 con Lámpara. Solo sirve para sombrear lo recordado. */
export function visionRadius(me) {
  return 3 + (me?.blessings?.includes('LAMPARA') ? 1 : 0);
}

export class BoardRenderer {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
  }

  /**
   * @param {object} state  { tiles: Map<"x,y", kind>, me, allies, enemies: Map, pendingOrder, armedMode }
   */
  draw(state) {
    const { ctx, canvas } = this;
    const cell = canvas.width / SIZE;
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = '#000';
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    const me = state.me;
    const radius = visionRadius(me);
    const inVision = (x, y) => me?.position && Math.abs(x - me.position.x) + Math.abs(y - me.position.y) <= radius;

    // Casillas conocidas.
    for (const [key, kind] of state.tiles) {
      const [x, y] = key.split(',').map(Number);
      ctx.fillStyle = TILE_COLORS[kind] ?? '#444';
      ctx.fillRect(x * cell + 1, y * cell + 1, cell - 2, cell - 2);
      const glyph = TILE_GLYPHS[kind];
      if (glyph) this.text(glyph, x, y, cell, '#f0e6d8', 0.42);
      if (!inVision(x, y)) {
        ctx.fillStyle = 'rgba(0,0,0,0.55)';
        ctx.fillRect(x * cell + 1, y * cell + 1, cell - 2, cell - 2);
      }
    }

    // Anuncios de ataque (telegraph) de la ronda siguiente.
    for (const enemy of state.enemies.values()) {
      for (const pos of enemy.telegraph ?? []) {
        ctx.fillStyle = 'rgba(226, 70, 60, 0.38)';
        ctx.fillRect(pos.x * cell + 1, pos.y * cell + 1, cell - 2, cell - 2);
        ctx.strokeStyle = '#ff6a5a';
        ctx.lineWidth = 2;
        ctx.strokeRect(pos.x * cell + 3, pos.y * cell + 3, cell - 6, cell - 6);
      }
    }

    // Enemigos.
    for (const enemy of state.enemies.values()) {
      if (!enemy.position) continue;
      const { x, y } = enemy.position;
      ctx.fillStyle = enemy.kind === 'CELADOR' ? '#a0202a' : '#7a2b2b';
      ctx.fillRect(x * cell + cell * 0.18, y * cell + cell * 0.18, cell * 0.64, cell * 0.64);
      this.text(ENEMY_LETTERS[enemy.kind] ?? '?', x, y, cell, '#fff', 0.36);
      this.facing(enemy.facing, x, y, cell, '#ffb0a8');
      this.hpPips(enemy.hp, enemy.maxHp, x, y, cell);
    }

    // Aliados (los que están en sigilo vienen sin posición y no se dibujan).
    for (const ally of state.allies) {
      if (!ally.position) continue;
      this.hero(ally, cell, false);
    }
    if (me?.position) this.hero(me, cell, true);

    // Orden pendiente propia: flecha desde el héroe.
    if (me?.position && state.pendingOrder) this.orderArrow(state.pendingOrder, me.position, cell);
    if (me?.position && state.armedMode) {
      ctx.strokeStyle = '#e8c15a';
      ctx.setLineDash([4, 3]);
      ctx.lineWidth = 2;
      ctx.strokeRect(me.position.x * cell + 2, me.position.y * cell + 2, cell - 4, cell - 4);
      ctx.setLineDash([]);
    }

    // Cuadrícula.
    ctx.strokeStyle = 'rgba(255,255,255,0.05)';
    ctx.lineWidth = 1;
    for (let i = 0; i <= SIZE; i++) {
      ctx.beginPath(); ctx.moveTo(i * cell, 0); ctx.lineTo(i * cell, canvas.height); ctx.stroke();
      ctx.beginPath(); ctx.moveTo(0, i * cell); ctx.lineTo(canvas.width, i * cell); ctx.stroke();
    }
  }

  hero(hero, cell, isMe) {
    const { ctx } = this;
    const { x, y } = hero.position;
    const cx = x * cell + cell / 2;
    const cy = y * cell + cell / 2;
    ctx.beginPath();
    ctx.arc(cx, cy, cell * 0.32, 0, Math.PI * 2);
    ctx.fillStyle = hero.state === 'ALIVE' ? (HERO_COLORS[hero.hero] ?? '#ccc') : '#555';
    ctx.fill();
    ctx.lineWidth = isMe ? 3 : 1.5;
    ctx.strokeStyle = isMe ? '#fff' : '#111';
    ctx.stroke();
    this.text(HERO_LETTERS[hero.hero] ?? '?', x, y, cell, '#111', 0.34);
    this.facing(hero.facing, x, y, cell, isMe ? '#fff' : '#ddd');
    this.hpPips(hero.hp, hero.maxHp, x, y, cell);
    if (hero.state === 'DOWN') this.text('✝', x, y, cell, '#e2675a', 0.3, -0.3);
  }

  facing(dir, x, y, cell, color) {
    const v = DIR_VECTORS[dir];
    if (!v) return;
    const { ctx } = this;
    const cx = x * cell + cell / 2 + v[0] * cell * 0.4;
    const cy = y * cell + cell / 2 + v[1] * cell * 0.4;
    const s = cell * 0.08;
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.moveTo(cx + v[0] * s, cy + v[1] * s);
    ctx.lineTo(cx - v[0] * s + v[1] * s, cy - v[1] * s + v[0] * s);
    ctx.lineTo(cx - v[0] * s - v[1] * s, cy - v[1] * s - v[0] * s);
    ctx.closePath();
    ctx.fill();
  }

  hpPips(hp, maxHp, x, y, cell) {
    if (!maxHp) return;
    const { ctx } = this;
    const w = cell * 0.8;
    const left = x * cell + cell * 0.1;
    const top = y * cell + cell - 6;
    ctx.fillStyle = '#300';
    ctx.fillRect(left, top, w, 4);
    ctx.fillStyle = hp / maxHp > 0.34 ? '#6cc28a' : '#e2675a';
    ctx.fillRect(left, top, w * Math.max(0, Math.min(1, hp / maxHp)), 4);
  }

  orderArrow(order, pos, cell) {
    const { ctx } = this;
    const cx = pos.x * cell + cell / 2;
    const cy = pos.y * cell + cell / 2;
    if (order.type === 'GUARD') {
      ctx.strokeStyle = '#8fd0ff';
      ctx.lineWidth = 3;
      ctx.beginPath();
      ctx.arc(cx, cy, cell * 0.44, 0, Math.PI * 2);
      ctx.stroke();
      return;
    }
    const v = DIR_VECTORS[order.direction];
    if (!v) return;
    const color = { MOVE: '#8fd0ff', ATTACK: '#ff6a5a', ABILITY: '#e8c15a', INTERACT: '#6cc28a' }[order.type] ?? '#fff';
    const tx = cx + v[0] * cell * 0.9;
    const ty = cy + v[1] * cell * 0.9;
    ctx.strokeStyle = color;
    ctx.fillStyle = color;
    ctx.lineWidth = 3;
    ctx.beginPath(); ctx.moveTo(cx, cy); ctx.lineTo(tx, ty); ctx.stroke();
    const s = cell * 0.14;
    ctx.beginPath();
    ctx.moveTo(tx + v[0] * s, ty + v[1] * s);
    ctx.lineTo(tx - v[0] * s + v[1] * s, ty - v[1] * s + v[0] * s);
    ctx.lineTo(tx - v[0] * s - v[1] * s, ty - v[1] * s - v[0] * s);
    ctx.closePath();
    ctx.fill();
  }

  text(str, x, y, cell, color, scale, dy = 0) {
    const { ctx } = this;
    ctx.fillStyle = color;
    ctx.font = `bold ${Math.round(cell * scale)}px system-ui, sans-serif`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(str, x * cell + cell / 2, y * cell + cell / 2 + dy * cell);
  }
}
