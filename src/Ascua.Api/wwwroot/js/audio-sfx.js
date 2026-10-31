// Recetas de efectos de sonido (fantasía oscura: piedra, metal, brasas). Cada receta recibe el instante de
// inicio `t` y el nodo de salida `out` (ver sfxOut) y devuelve cuánto dura, para liberar la voz a tiempo.
import { bell, midi, noise, pick, rand, tone } from './audio-engine.js';

const LP = (freq, to, q) => ({ type: 'lowpass', freq, to, q });
const BP = (freq, to, q = 1.2) => ({ type: 'bandpass', freq, to, q });
const HP = (freq) => ({ type: 'highpass', freq });

function crackles(t, out, { count, span, peak = 0.25, freq = 3500 }) {
  for (let i = 0; i < count; i++) {
    noise({ t: t + Math.random() * span, dur: rand(0.008, 0.025), peak: peak * rand(0.4, 1), filter: HP(freq * rand(0.7, 1.4)), out });
  }
}

/** Metal con dos sierras levemente desafinadas y filtro que se abre (fanfarrias y golpes del jefe). */
function brass(freq, t, dur, peak, out) {
  for (const detune of [-7, 7]) {
    tone({ type: 'sawtooth', freq, t, dur, attack: 0.04, peak, detune, filter: { type: 'lowpass', freq: 300, to: 2400, time: 0.12, back: 700, q: 1.5 }, out });
  }
}

export const SFX = {
  // ───────── interfaz ─────────
  uiHover(t, out) {
    tone({ freq: 1760, freqEnd: 1980, t, dur: 0.05, attack: 0.004, peak: 0.05, out });
    return 0.06;
  },
  uiClick(t, out) {
    noise({ t, dur: 0.025, peak: 0.45, filter: BP(2400, null, 3), out });
    tone({ type: 'triangle', freq: 540, freqEnd: 360, t, dur: 0.07, peak: 0.2, out });
    return 0.08;
  },
  orderSent(t, out) {
    noise({ t, dur: 0.2, attack: 0.05, peak: 0.35, filter: BP(500, 2600, 1.5), out });
    tone({ type: 'triangle', freq: midi(67), freqEnd: midi(74), t, dur: 0.16, peak: 0.14, out });
    return 0.22;
  },
  orderAcked(t, out) {
    bell({ freq: midi(86), t, dur: 0.7, peak: 0.06, out });
    bell({ freq: midi(93), t: t + 0.07, dur: 0.8, peak: 0.05, out });
    return 0.9;
  },
  orderRejected(t, out) {
    for (const [f, dt] of [[147, 0], [156, 0], [117, 0.13], [124, 0.13]]) {
      tone({ type: 'square', freq: f, t: t + dt, dur: 0.18, peak: 0.07, filter: LP(900), out });
    }
    return 0.35;
  },
  allyDecided(t, out) {
    tone({ freq: midi(81), t, dur: 0.09, peak: 0.035, out });
    return 0.1;
  },
  roundOpened(t, out) {
    bell({ freq: midi(62), t, dur: 2.2, peak: 0.07, out });
    tone({ freq: midi(38), t, dur: 1.1, attack: 0.02, peak: 0.12, out });
    return 2.2;
  },
  /** Latido doble al entrar en los últimos 5 s. */
  lowTime(t, out) {
    for (const dt of [0, 0.2]) tone({ freq: 75, freqEnd: 42, t: t + dt, dur: 0.16, peak: dt ? 0.35 : 0.5, out });
    return 0.4;
  },
  tick(t, out, { urgency = 0 } = {}) {
    const f = 900 + urgency * 220;
    tone({ freq: f, t, dur: 0.05, peak: 0.26, out });
    noise({ t, dur: 0.015, peak: 0.3, filter: BP(f * 3, null, 4), out });
    if (urgency >= 4) bell({ freq: midi(81), t, dur: 0.6, peak: 0.05, metal: true, out });
    return 0.6;
  },
  /** La ronda se resuelve: brasa que inspira y golpe sordo. */
  resolve(t, out) {
    noise({ t, dur: 0.35, attack: 0.26, peak: 0.16, filter: LP(220, 2200), curve: 'lin', out });
    tone({ freq: 110, freqEnd: 40, t: t + 0.28, dur: 0.28, peak: 0.3, out });
    return 0.6;
  },

  // ───────── draft y partida ─────────
  draftOffer(t, out) {
    [74, 77, 81, 84, 88].forEach((n, i) => bell({ freq: midi(n), t: t + i * 0.09, dur: 1.6, peak: 0.05, out }));
    noise({ t, dur: 1.3, attack: 0.7, peak: 0.035, filter: HP(6000), curve: 'lin', out });
    return 1.8;
  },
  draftPicked(t, out) {
    for (const n of [62, 69, 74, 78]) tone({ type: 'triangle', freq: midi(n), t, dur: 1.3, attack: 0.02, peak: 0.055, out });
    noise({ t, dur: 0.4, attack: 0.1, peak: 0.12, filter: BP(600, 3000), out });
    bell({ freq: midi(86), t: t + 0.05, dur: 1.2, peak: 0.05, out });
    return 1.4;
  },
  matchStart(t, out) {
    bell({ freq: midi(38), t, dur: 4, peak: 0.22, out });
    noise({ t, dur: 1.2, attack: 0.4, peak: 0.12, filter: BP(300, 1800, 0.8), out });
    return 4;
  },
  /** minor: el Celador cayó pero no ganaste las ascuas; la fanfarria cierra en menor. */
  victory(t, out, { minor = false } = {}) {
    const chords = [[62, 66, 69], [67, 71, 74], [69, 73, 76], minor ? [74, 77, 81, 86] : [74, 78, 81, 86]];
    chords.forEach((c, i) => {
      const at = t + i * 0.42;
      const dur = i === 3 ? 2.8 : 0.5;
      for (const n of c) brass(midi(n), at, dur, 0.05, out);
      tone({ freq: 92, freqEnd: 70, t: at, dur: 0.4, peak: 0.4, out });
    });
    for (let i = 0; i < 8; i++) bell({ freq: midi(pick([86, 88, 90, 93, 95, 98])), t: t + 1.3 + i * 0.12, dur: 1.2, peak: 0.035, out });
    return 4.5;
  },
  defeat(t, out) {
    const chords = [[50, 53, 57], [46, 50, 53], [43, 46, 50], [38, 50, 53, 57]];
    chords.forEach((c, i) => {
      const at = t + i * 1.1;
      for (const n of c) tone({ type: 'triangle', freq: midi(n), t: at, dur: i === 3 ? 4 : 1.5, attack: 0.25, peak: 0.07, filter: LP(1400), out });
    });
    for (const dt of [0, 2.2, 4.4]) bell({ freq: midi(31), t: t + dt, dur: 3.5, peak: 0.18, out });
    return 7.5;
  },

  // ───────── eventos de la ronda ─────────
  step(t, out) {
    noise({ t, dur: 0.09, peak: 0.3, filter: LP(rand(500, 900)), out });
    tone({ freq: rand(80, 100), freqEnd: 50, t, dur: 0.08, peak: 0.2, out });
    noise({ t: t + 0.01, dur: 0.04, peak: 0.04, filter: BP(2600, null, 2), out });
    return 0.12;
  },
  bounce(t, out) {
    tone({ freq: 110, freqEnd: 45, t, dur: 0.22, peak: 0.45, out });
    noise({ t, dur: 0.14, peak: 0.35, filter: LP(500), out });
    noise({ t: t + 0.05, dur: 0.25, attack: 0.02, peak: 0.05, filter: BP(1800), out });
    return 0.3;
  },
  /** Yunque: martillazo sordo con resonancia de yunque. */
  attackYunque(t, out) {
    tone({ freq: 140, freqEnd: 45, t, dur: 0.28, attack: 0.003, peak: 0.6, out });
    noise({ t, dur: 0.18, peak: 0.45, filter: LP(900, 200), out });
    bell({ freq: 310, t: t + 0.01, dur: 0.6, peak: 0.05, metal: true, out });
    return 0.6;
  },
  /** Chispa: disparo de brasa que chisporrotea. */
  attackChispa(t, out) {
    tone({ type: 'sawtooth', freq: 1900, freqEnd: 220, t, dur: 0.2, peak: 0.12, filter: LP(5000, 800), out });
    tone({ freq: 2600, freqEnd: 3600, t, dur: 0.08, peak: 0.06, out });
    noise({ t, dur: 0.12, peak: 0.12, filter: BP(1200, 4000), out });
    crackles(t + 0.04, out, { count: 7, span: 0.3, peak: 0.22 });
    return 0.4;
  },
  /** Cuervo: tajo de hoja con brillo metálico. */
  attackCuervo(t, out) {
    noise({ t, dur: 0.16, attack: 0.025, peak: 0.45, filter: BP(4200, 900, 2), out });
    tone({ freq: 3150, t: t + 0.03, dur: 0.28, peak: 0.04, out });
    tone({ freq: 4730, t: t + 0.04, dur: 0.22, peak: 0.03, out });
    return 0.32;
  },
  /** Raíz: latigazo de raíces, crujido de madera y hojas. */
  attackRaiz(t, out) {
    tone({ type: 'triangle', freq: 190, freqEnd: 115, t, dur: 0.35, peak: 0.28, filter: LP(650, null, 8), out });
    tone({ freq: 90, freqEnd: 55, t, dur: 0.2, peak: 0.35, out });
    for (let i = 0; i < 6; i++) noise({ t: t + 0.02 + i * 0.035, dur: 0.03, peak: 0.12 * rand(0.5, 1), filter: BP(rand(1100, 2400), null, 2), out });
    return 0.4;
  },
  attackEnemy(t, out, { boss = false } = {}) {
    const f = boss ? 80 : 160;
    tone({ type: 'sawtooth', freq: f, freqEnd: f * 0.45, t, dur: boss ? 0.5 : 0.3, peak: boss ? 0.3 : 0.2, filter: LP(boss ? 500 : 750, null, 4), out });
    noise({ t, dur: 0.15, peak: 0.25, filter: LP(1500), out });
    if (boss) bell({ freq: 98, t, dur: 1.2, peak: 0.12, metal: true, out });
    return boss ? 1.2 : 0.35;
  },
  damage(t, out, { onMe = false, onEnemy = false } = {}) {
    noise({ t, dur: 0.12, peak: 0.5, filter: LP(2600, 300), out });
    tone({ freq: 180, freqEnd: 60, t, dur: 0.15, peak: 0.4, out });
    if (onMe) {
      tone({ freq: 65, freqEnd: 34, t, dur: 0.35, peak: 0.55, out });
      tone({ type: 'square', freq: 98, freqEnd: 73, t, dur: 0.22, peak: 0.06, filter: LP(600), out });
    }
    if (onEnemy) tone({ type: 'square', freq: 300, freqEnd: 120, t, dur: 0.12, peak: 0.1, filter: LP(900), out });
    return 0.4;
  },
  block(t, out) {
    noise({ t, dur: 0.04, peak: 0.4, filter: HP(4000), out });
    bell({ freq: 820, t, dur: 0.75, peak: 0.16, metal: true, out });
    bell({ freq: 1240, t: t + 0.005, dur: 0.45, peak: 0.07, metal: true, out });
    return 0.8;
  },
  heal(t, out) {
    [69, 74, 78, 81].forEach((n, i) => {
      tone({ freq: midi(n), t: t + i * 0.07, dur: 0.9, attack: 0.02, peak: 0.08, out });
      tone({ type: 'triangle', freq: midi(n + 12), t: t + i * 0.07, dur: 0.5, attack: 0.02, peak: 0.025, out });
    });
    noise({ t, dur: 0.7, attack: 0.3, peak: 0.04, filter: HP(6500), curve: 'lin', out });
    return 1.1;
  },
  downed(t, out, { onMe = false } = {}) {
    tone({ type: 'triangle', freq: 440, freqEnd: 110, t, dur: 0.7, peak: 0.16, filter: LP(1200), out });
    tone({ freq: 80, freqEnd: 38, t: t + 0.1, dur: 0.4, peak: 0.45, out });
    noise({ t: t + 0.1, dur: 0.5, peak: 0.18, filter: LP(400), out });
    if (onMe) for (const n of [50, 53, 57]) tone({ freq: midi(n), t: t + 0.2, dur: 1.8, attack: 0.15, peak: 0.06, out });
    return onMe ? 2 : 0.8;
  },
  revived(t, out) {
    noise({ t, dur: 0.6, attack: 0.5, peak: 0.12, filter: BP(300, 3200), curve: 'lin', out });
    bell({ freq: midi(81), t: t + 0.5, dur: 1.2, peak: 0.06, out });
    bell({ freq: midi(88), t: t + 0.58, dur: 1.2, peak: 0.05, out });
    return 1.8;
  },
  extinguished(t, out) {
    tone({ type: 'sawtooth', freq: 220, freqEnd: 55, t, dur: 2, peak: 0.14, filter: LP(900, 120), out });
    noise({ t, dur: 1.5, attack: 0.1, peak: 0.18, filter: LP(1200, 200), out });
    bell({ freq: 147, t, dur: 3, peak: 0.1, out });
    return 3;
  },
  enemyKilled(t, out, { boss = false } = {}) {
    noise({ t, dur: 0.3, peak: 0.45, filter: BP(1300, 280, 1.5), out });
    tone({ type: 'square', freq: 620, freqEnd: 80, t, dur: 0.3, peak: 0.08, filter: LP(1500), out });
    noise({ t: t + 0.08, dur: 0.6, attack: 0.05, peak: 0.16, filter: LP(3000, 150), out });
    if (boss) {
      tone({ freq: 70, freqEnd: 28, t, dur: 2, peak: 0.6, out });
      bell({ freq: 73, t, dur: 5, peak: 0.25, out });
      return 5;
    }
    return 0.7;
  },
  enemySpawned(t, out) {
    tone({ type: 'triangle', freq: 200, freqEnd: 420, t, dur: 0.6, attack: 0.2, peak: 0.07, filter: LP(900), out });
    noise({ t, dur: 0.6, attack: 0.3, peak: 0.07, filter: BP(800), out });
    return 0.7;
  },
  chest(t, out) {
    noise({ t, dur: 0.03, peak: 0.35, filter: HP(2000), out });
    tone({ type: 'sawtooth', freq: 210, freqEnd: 290, t: t + 0.03, dur: 0.38, peak: 0.08, filter: { type: 'bandpass', freq: 900, to: 1600, q: 10 }, out });
    tone({ freq: 125, freqEnd: 70, t: t + 0.36, dur: 0.15, peak: 0.3, out });
    [86, 90, 93].forEach((n, i) => bell({ freq: midi(n), t: t + 0.42 + i * 0.06, dur: 0.8, peak: 0.035, out }));
    return 1.3;
  },
  embers(t, out, { amount = 1 } = {}) {
    const n = Math.min(5, Math.max(1, amount));
    let at = t;
    for (let i = 0; i < n; i++) {
      bell({ freq: midi(pick([86, 88, 90, 93, 95, 98])), t: at, dur: 0.5, peak: 0.06, out });
      at += rand(0.055, 0.09);
    }
    return at - t + 0.5;
  },
  embersStolen(t, out) {
    noise({ t, dur: 0.3, attack: 0.03, peak: 0.12, filter: BP(2200, 500), out });
    [93, 89, 86, 82].forEach((n, i) => bell({ freq: midi(n), t: t + i * 0.07, dur: 0.45, peak: 0.05, metal: true, out }));
    return 0.8;
  },
  blessing(t, out) {
    for (const n of [62, 66, 69, 74]) tone({ type: 'triangle', freq: midi(n), t, dur: 1.6, attack: 0.3, peak: 0.05, out });
    [81, 86, 90].forEach((n, i) => bell({ freq: midi(n), t: t + 0.25 + i * 0.1, dur: 1.2, peak: 0.04, out }));
    return 1.7;
  },
  trap(t, out) {
    noise({ t, dur: 0.02, peak: 0.45, filter: HP(3000), out });
    tone({ type: 'square', freq: 900, freqEnd: 150, t, dur: 0.08, peak: 0.12, filter: LP(3000), out });
    tone({ freq: 420, freqEnd: 260, t: t + 0.03, dur: 0.3, peak: 0.08, vibrato: { rate: 28, depth: 25 }, out });
    bell({ freq: 610, t, dur: 0.3, peak: 0.05, metal: true, out });
    return 0.4;
  },
  fire(t, out) {
    noise({ t, dur: 0.65, attack: 0.15, peak: 0.3, filter: BP(300, 1800, 0.8), out });
    noise({ t, dur: 0.9, attack: 0.05, peak: 0.16, filter: LP(300), out });
    crackles(t + 0.05, out, { count: 10, span: 0.8, peak: 0.28 });
    return 1;
  },
  frozen(t, out) {
    noise({ t, dur: 0.05, peak: 0.35, filter: HP(5000), out });
    [96, 100, 103, 107].forEach((n, i) => tone({ freq: midi(n) * rand(0.995, 1.005), t: t + 0.02 + i * 0.03, dur: 0.8, peak: 0.04, out }));
    tone({ freq: 6000, freqEnd: 3800, t, dur: 0.3, peak: 0.025, out });
    return 0.9;
  },
  bossPhase(t, out) {
    for (const [f, d] of [[55, 0], [55.6, 0], [82.4, 0]]) {
      tone({ type: 'sawtooth', freq: f + d, t, dur: 1.9, attack: 0.25, peak: 0.12, filter: { type: 'lowpass', freq: 180, to: 1500, time: 0.6, back: 200, q: 3 }, out });
    }
    bell({ freq: 98, t: t + 0.1, dur: 4, peak: 0.25, out });
    noise({ t, dur: 1.5, attack: 0.3, peak: 0.18, filter: LP(600), out });
    return 4;
  },
  floorEntered(t, out) {
    bell({ freq: 73, t, dur: 5, peak: 0.22, out });
    noise({ t, dur: 1.6, attack: 0.5, peak: 0.13, filter: BP(2000, 200), out });
    for (const n of [50, 57, 62]) tone({ type: 'triangle', freq: midi(n), t: t + 0.3, dur: 2.5, attack: 0.8, peak: 0.04, out });
    return 5;
  },
  defaultGuard(t, out) {
    tone({ freq: 300, freqEnd: 220, t, dur: 0.1, peak: 0.12, out });
    noise({ t, dur: 0.03, peak: 0.08, filter: BP(1500), out });
    return 0.12;
  },
  preview(t, out) {
    bell({ freq: midi(86), t, dur: 0.8, peak: 0.08, out });
    bell({ freq: midi(93), t: t + 0.08, dur: 0.8, peak: 0.06, out });
    return 0.9;
  },
};
