// Piezas de efectos para el tablero: suavizados, una línea de tiempo para encadenar la resolución de
// una ronda, un pool de partículas con tope (arreglos tipados, sin objetos por frame) y textos flotantes.

export const ease = {
  linear: (t) => t,
  outCubic: (t) => 1 - (1 - t) ** 3,
  inOutQuad: (t) => (t < 0.5 ? 2 * t * t : 1 - (-2 * t + 2) ** 2 / 2),
  outBack: (t) => { const c = 1.6; return 1 + (c + 1) * (t - 1) ** 3 + c * (t - 1) ** 2; },
  /** Va y vuelve: 0 → 1 → 0. */
  pulse: (t) => Math.sin(Math.PI * t),
};

export const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);

/**
 * Línea de tiempo en milisegundos relativa a `start(now)`. Cada paso es un instante (`at`) o un tramo
 * (`tween`, con update(p) de 0 a 1 y un final opcional). `finish()` aplica de golpe lo pendiente: así una
 * resolución nueva nunca deja la anterior a medias.
 */
export class Timeline {
  constructor() {
    this.items = [];
    this.origin = 0;
    this.running = false;
    this.length = 0;
  }

  at(t, fn) { this.items.push({ t, dur: 0, update: null, end: fn, state: 0 }); this.length = Math.max(this.length, t); return this; }

  tween(t, dur, update, end = null) {
    this.items.push({ t, dur: Math.max(1, dur), update, end, state: 0 });
    this.length = Math.max(this.length, t + dur);
    return this;
  }

  start(now) { this.origin = now; this.running = this.items.length > 0; }

  update(now) {
    if (!this.running) return false;
    const elapsed = now - this.origin;
    let pending = false;
    for (const it of this.items) {
      if (it.state === 2) continue;
      if (elapsed < it.t) { pending = true; continue; }
      const p = it.dur ? clamp01((elapsed - it.t) / it.dur) : 1;
      it.state = 1;
      it.update?.(p);
      if (p >= 1) { it.state = 2; it.end?.(); } else pending = true;
    }
    if (!pending) this.clear();
    return pending;
  }

  finish() {
    // Los pasos se aplican en orden de inicio, como habrían ocurrido.
    const items = this.items.slice().sort((a, b) => a.t - b.t);
    for (const it of items) {
      if (it.state === 2) continue;
      it.state = 2;
      it.update?.(1);
      it.end?.();
    }
    this.clear();
  }

  clear() { this.items = []; this.running = false; this.length = 0; }
}

/** Pool de partículas: estructura de arreglos con tope; al llenarse se descartan las nuevas. */
export class Particles {
  constructor(cap = 800) {
    this.cap = cap;
    this.n = 0;
    this.x = new Float32Array(cap);
    this.y = new Float32Array(cap);
    this.vx = new Float32Array(cap);
    this.vy = new Float32Array(cap);
    this.life = new Float32Array(cap);
    this.max = new Float32Array(cap);
    this.size = new Float32Array(cap);
    this.grav = new Float32Array(cap);
    this.drag = new Float32Array(cap);
    this.glow = new Uint8Array(cap);
    this.color = new Array(cap).fill('#fff');
  }

  /** Posiciones y velocidades en unidades de casilla (por segundo); `size` en fracción de casilla. */
  spawn(x, y, vx, vy, life, size, color, grav = 0, drag = 0, glowing = false) {
    if (this.n >= this.cap) return;
    const i = this.n++;
    this.x[i] = x; this.y[i] = y; this.vx[i] = vx; this.vy[i] = vy;
    this.life[i] = life; this.max[i] = life; this.size[i] = size;
    this.grav[i] = grav; this.drag[i] = drag; this.glow[i] = glowing ? 1 : 0;
    this.color[i] = color;
  }

  /** Estallido radial. */
  burst(x, y, count, { speed = 2, life = 0.6, size = 0.06, colors = ['#fff'], grav = 0, drag = 2, glow = false, up = 0 } = {}) {
    for (let k = 0; k < count; k++) {
      const a = Math.random() * Math.PI * 2;
      const s = speed * (0.35 + Math.random() * 0.65);
      this.spawn(x, y, Math.cos(a) * s, Math.sin(a) * s - up, life * (0.6 + Math.random() * 0.6),
        size * (0.6 + Math.random() * 0.8), colors[k % colors.length], grav, drag, glow);
    }
  }

  /** Chorro a lo largo de un segmento (el lazo de la Calidez, la estela de una Brasa): cada partícula sale de un punto del tramo y deriva hacia el final. */
  stream(x0, y0, x1, y1, count, { life = 0.7, size = 0.05, colors = ['#fff'], jitter = 0.12, speed = 0.6, drag = 1.5, glow = true } = {}) {
    const len = Math.hypot(x1 - x0, y1 - y0) || 1;
    const ux = (x1 - x0) / len;
    const uy = (y1 - y0) / len;
    for (let k = 0; k < count; k++) {
      const p = Math.random();
      this.spawn(x0 + (x1 - x0) * p + (Math.random() - 0.5) * jitter, y0 + (y1 - y0) * p + (Math.random() - 0.5) * jitter,
        ux * speed + (Math.random() - 0.5) * 0.3, uy * speed - 0.2 - Math.random() * 0.3, life * (0.6 + Math.random() * 0.6),
        size * (0.6 + Math.random() * 0.8), colors[k % colors.length], 0, drag, glow);
    }
  }

  /** Remolino: partículas en un anillo que giran y caen hacia el centro (inward) o salen de él (portal, renacer). */
  vortex(x, y, count, { radius = 0.8, life = 0.8, size = 0.06, colors = ['#fff'], inward = true, spin = 2.4, glow = true } = {}) {
    for (let k = 0; k < count; k++) {
      const a = (k / count) * Math.PI * 2 + Math.random() * 0.4;
      const r = inward ? radius * (0.8 + Math.random() * 0.4) : radius * 0.15;
      const px = x + Math.cos(a) * r;
      const py = y + Math.sin(a) * r * 0.6;
      // Velocidad tangencial más una radial (hacia adentro o hacia afuera).
      const radial = (inward ? -1 : 1) * radius / life;
      const vx = -Math.sin(a) * spin + Math.cos(a) * radial;
      const vy = (Math.cos(a) * spin + Math.sin(a) * radial) * 0.6;
      this.spawn(px, py, vx, vy, life * (0.7 + Math.random() * 0.5), size * (0.6 + Math.random() * 0.8), colors[k % colors.length], 0, 0.6, glow);
    }
  }

  update(dt) {
    let i = 0;
    while (i < this.n) {
      this.life[i] -= dt;
      if (this.life[i] <= 0) {
        const last = --this.n;
        if (i !== last) {
          this.x[i] = this.x[last]; this.y[i] = this.y[last]; this.vx[i] = this.vx[last]; this.vy[i] = this.vy[last];
          this.life[i] = this.life[last]; this.max[i] = this.max[last]; this.size[i] = this.size[last];
          this.grav[i] = this.grav[last]; this.drag[i] = this.drag[last]; this.glow[i] = this.glow[last];
          this.color[i] = this.color[last];
        }
        continue;
      }
      const k = 1 - Math.min(1, this.drag[i] * dt);
      this.vx[i] *= k;
      this.vy[i] = this.vy[i] * k + this.grav[i] * dt;
      this.x[i] += this.vx[i] * dt;
      this.y[i] += this.vy[i] * dt;
      i++;
    }
  }

  /** Dos pasadas: las normales y luego las brillantes en modo aditivo. */
  draw(g, cell, ox = 0, oy = 0) {
    if (!this.n) return;
    for (let pass = 0; pass < 2; pass++) {
      g.globalCompositeOperation = pass ? 'lighter' : 'source-over';
      for (let i = 0; i < this.n; i++) {
        if (this.glow[i] !== pass) continue;
        const t = this.life[i] / this.max[i];
        const s = Math.max(1, this.size[i] * cell * (0.4 + 0.6 * t));
        g.globalAlpha = Math.min(1, t * 1.6);
        g.fillStyle = this.color[i];
        g.fillRect(ox + this.x[i] * cell - s / 2, oy + this.y[i] * cell - s / 2, s, s);
      }
    }
    g.globalAlpha = 1;
    g.globalCompositeOperation = 'source-over';
  }

  clear() { this.n = 0; }
}

/** Textos flotantes («-2», «+1 ✦»…): pool fijo; si se llena, se reemplaza el más viejo. */
export class Floaters {
  constructor(cap = 40) {
    this.items = Array.from({ length: cap }, () => ({ on: false, text: '', x: 0, y: 0, tx: 0, ty: 0, fly: false, life: 0, max: 1, color: '#fff', size: 0.3, delay: 0 }));
  }

  add(text, x, y, { color = '#fff', size = 0.32, life = 1.1, fly = false, tx = 0, ty = 0, delay = 0 } = {}) {
    let slot = this.items.find((f) => !f.on);
    if (!slot) slot = this.items.reduce((a, b) => (a.life < b.life ? a : b));
    // Si ya hay textos recientes en el mismo lugar, el nuevo se apila encima en vez de taparlos.
    for (const f of this.items) {
      if (f.on && f !== slot && Math.abs(f.sx - x) < 0.4 && Math.abs(f.sy - y) < 0.25 && f.life > f.max * 0.4) y -= 0.28;
    }
    Object.assign(slot, { on: true, text, x, y, sx: x, sy: y, tx, ty, fly, life, max: life, color, size, delay });
  }

  update(dt) {
    for (const f of this.items) {
      if (!f.on) continue;
      if (f.delay > 0) { f.delay -= dt; continue; }
      f.life -= dt;
      if (f.life <= 0) { f.on = false; continue; }
      const t = 1 - f.life / f.max;
      if (f.fly && t > 0.35) {
        // Sube y luego vuela hacia su destino (p. ej. el marcador de ascuas, arriba).
        const k = ease.inOutQuad((t - 0.35) / 0.65);
        f.x = f.sx + (f.tx - f.sx) * k;
        f.y = f.sy - 0.45 + (f.ty - f.sy + 0.45) * k;
      } else {
        f.y = f.sy - ease.outCubic(Math.min(1, t / 0.35)) * 0.45;
      }
    }
  }

  draw(g, cell) {
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.lineJoin = 'round';
    for (const f of this.items) {
      if (!f.on || f.delay > 0) continue;
      const t = 1 - f.life / f.max;
      const pop = t < 0.15 ? ease.outBack(t / 0.15) : 1;
      const px = Math.round(f.size * cell * pop);
      if (px < 2) continue;
      g.globalAlpha = t > 0.75 ? (1 - t) / 0.25 : 1;
      g.font = `900 ${px}px system-ui, sans-serif`;
      g.lineWidth = Math.max(2, px * 0.2);
      g.strokeStyle = '#120806';
      g.strokeText(f.text, f.x * cell, f.y * cell);
      g.fillStyle = f.color;
      g.fillText(f.text, f.x * cell, f.y * cell);
    }
    g.globalAlpha = 1;
  }

  clear() { this.items.forEach((f) => { f.on = false; }); }
}
