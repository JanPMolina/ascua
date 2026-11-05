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
  // ───────── versión 3 ─────────
  voteCast(t, out) {
    noise({ t, dur: 0.05, peak: 0.5, filter: BP(1400, null, 2), out });
    tone({ type: 'triangle', freq: 220, freqEnd: 140, t, dur: 0.16, peak: 0.35, out });
    return 0.2;
  },
  interludeOpen(t, out) {
    bell({ freq: midi(50), t, dur: 1.8, peak: 0.22, out });
    bell({ freq: midi(57), t: t + 0.18, dur: 1.4, peak: 0.14, out });
    return 2;
  },
  verdictGuilty(t, out) {
    noise({ t, dur: 0.1, peak: 0.7, filter: LP(900, 200), out });
    tone({ type: 'sawtooth', freq: 130, freqEnd: 65, t, dur: 0.5, peak: 0.3, filter: LP(800, 200), out });
    bell({ freq: midi(38), t: t + 0.08, dur: 1.6, peak: 0.25, out });
    return 1.7;
  },
  verdictInnocent(t, out) {
    tone({ type: 'triangle', freq: 392, t, dur: 0.22, peak: 0.2, out });
    tone({ type: 'triangle', freq: 330, t: t + 0.16, dur: 0.34, peak: 0.2, out });
    return 0.55;
  },
  verdictNone(t, out) {
    tone({ type: 'sine', freq: 262, t, dur: 0.3, peak: 0.16, out });
    return 0.35;
  },
  push(t, out) {
    noise({ t, dur: 0.12, peak: 0.6, filter: LP(1400, 300), out });
    tone({ type: 'triangle', freq: 150, freqEnd: 70, t, dur: 0.18, peak: 0.45, out });
    return 0.22;
  },
  flood(t, out) {
    noise({ t, dur: 1.2, attack: 0.3, peak: 0.35, filter: BP(500, 1100, 0.8), out });
    noise({ t: t + 0.2, dur: 0.9, attack: 0.2, peak: 0.2, filter: BP(1400, 700, 1), out });
    return 1.3;
  },
  sabotage(t, out) {
    tone({ type: 'sawtooth', freq: 880, freqEnd: 220, t, dur: 0.16, peak: 0.16, filter: BP(1500, 500, 2), out });
    noise({ t: t + 0.04, dur: 0.1, peak: 0.45, filter: HP(1800), out });
    tone({ type: 'sine', freq: 185, t: t + 0.1, dur: 0.5, peak: 0.16, detune: -30, out });
    return 0.65;
  },
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
  /** Errata: un plumazo seco que rasga el papel. */
  attackErrata(t, out) {
    noise({ t, dur: 0.14, attack: 0.01, peak: 0.4, filter: BP(3400, 1200, 3), out });
    tone({ freq: 900, freqEnd: 2200, t, dur: 0.1, peak: 0.05, out });
    return 0.25;
  },
  /** Anteayer: golpe opaco con un eco que se aleja. */
  attackAnteayer(t, out) {
    for (let i = 0; i < 3; i++) {
      tone({ type: 'triangle', freq: 320, freqEnd: 150, t: t + i * 0.11, dur: 0.14, peak: 0.32 / (i * 1.8 + 1), filter: LP(1500), out });
    }
    return 0.5;
  },
  /** Hilván: dos pinchazos de aguja, uno para cada cuerpo. */
  attackHilvan(t, out) {
    for (let i = 0; i < 2; i++) {
      noise({ t: t + i * 0.07, dur: 0.05, peak: 0.35, filter: BP(5200, null, 3), out });
      tone({ freq: 2100 - i * 300, t: t + i * 0.07, dur: 0.1, peak: 0.05, out });
    }
    return 0.3;
  },
  /** Polizón: zarpazo corto y húmedo. */
  attackPolizon(t, out) {
    noise({ t, dur: 0.1, attack: 0.008, peak: 0.45, filter: BP(1700, 500, 2), out });
    tone({ type: 'sawtooth', freq: 240, freqEnd: 90, t, dur: 0.14, peak: 0.12, filter: LP(900), out });
    return 0.2;
  },
  /** La Escucha: una nota aguda que cruza la sala a tientas. */
  attackEscucha(t, out) {
    tone({ freq: 1300, freqEnd: 2500, t, dur: 0.22, peak: 0.1, out });
    tone({ type: 'triangle', freq: 650, freqEnd: 1250, t, dur: 0.22, peak: 0.08, out });
    noise({ t: t + 0.15, dur: 0.06, peak: 0.18, filter: BP(2800, null, 2), out });
    return 0.35;
  },
  /** La Usurera: bastonazo seco con tintineo de monedas. */
  attackUsurera(t, out) {
    tone({ freq: 220, freqEnd: 80, t, dur: 0.12, peak: 0.34, out });
    for (let i = 0; i < 3; i++) bell({ freq: rand(2600, 3400), t: t + 0.03 + i * 0.03, dur: 0.16, peak: 0.03, metal: true, out });
    return 0.32;
  },
  /** El Avaro: puñetazo con el saco, que suena a monedas y brasas. */
  attackAvaro(t, out) {
    tone({ freq: 110, freqEnd: 50, t, dur: 0.2, peak: 0.5, out });
    noise({ t, dur: 0.1, peak: 0.25, filter: LP(1000), out });
    crackles(t + 0.03, out, { count: 4, span: 0.2, peak: 0.16 });
    return 0.32;
  },
  /** El Mampostero: mazazo sobre piedra. */
  attackMampostero(t, out) {
    tone({ freq: 140, freqEnd: 45, t, dur: 0.28, attack: 0.003, peak: 0.6, out });
    noise({ t, dur: 0.18, peak: 0.45, filter: LP(900, 200), out });
    for (let i = 0; i < 4; i++) noise({ t: t + 0.05 + i * 0.03, dur: 0.03, peak: 0.1, filter: BP(rand(900, 1900), null, 2), out });
    return 0.5;
  },
  /** El Campanero: un tañido corto que llega lejos. */
  attackCampanero(t, out) {
    bell({ freq: 520, t, dur: 0.7, peak: 0.12, metal: true, out });
    tone({ freq: 160, freqEnd: 100, t, dur: 0.12, peak: 0.18, out });
    return 0.7;
  },

  // ───── eventos de los nueve héroes ─────
  /** Eco de Anteayer: el golpe repetido, sin cuerpo, con mucha cola. */
  echoFired(t, out) {
    for (let i = 0; i < 4; i++) tone({ freq: 760 - i * 90, freqEnd: 380 - i * 40, t: t + i * 0.09, dur: 0.16, peak: 0.1 / (i + 1), filter: LP(2400), out });
    noise({ t, dur: 0.3, attack: 0.04, peak: 0.08, filter: BP(2600, 700, 2), out });
    return 0.7;
  },
  /** Volver: el tiempo va al revés, con un ataque que crece en lugar de caer. */
  rewind(t, out) {
    tone({ type: 'triangle', freq: 300, freqEnd: 1400, t, dur: 0.45, attack: 0.35, peak: 0.12, out });
    noise({ t, dur: 0.45, attack: 0.38, peak: 0.12, filter: BP(1200, 5200, 2), out });
    bell({ freq: 880, t: t + 0.42, dur: 0.5, peak: 0.05, out });
    return 0.95;
  },
  /** Corregir: pluma que tacha, y el anuncio se va de lado. */
  correct(t, out) {
    noise({ t, dur: 0.22, attack: 0.02, peak: 0.28, filter: BP(2200, 5200, 4), out });
    tone({ freq: 700, freqEnd: 1300, t: t + 0.05, dur: 0.18, peak: 0.07, out });
    return 0.4;
  },
  /** Tensar: un hilo que se pone tirante y corta. */
  tense(t, out) {
    tone({ type: 'sawtooth', freq: 180, freqEnd: 1500, t, dur: 0.2, attack: 0.15, peak: 0.08, filter: LP(3600), out });
    noise({ t: t + 0.2, dur: 0.1, peak: 0.42, filter: BP(4200, null, 2), out });
    return 0.4;
  },
  /** Abordar: se cuela con un sorbo viscoso. */
  board(t, out) {
    tone({ freq: 420, freqEnd: 120, t, dur: 0.3, peak: 0.25, filter: LP(1100, null, 6), out });
    noise({ t, dur: 0.24, attack: 0.05, peak: 0.16, filter: LP(1000, 300), out });
    return 0.35;
  },
  /** Polizón reaparece: el monstruo lo escupe. */
  unboard(t, out) {
    tone({ freq: 130, freqEnd: 460, t, dur: 0.2, peak: 0.24, filter: LP(1200, null, 6), out });
    noise({ t: t + 0.12, dur: 0.1, peak: 0.2, filter: BP(1500), out });
    return 0.3;
  },
  drain(t, out) {
    tone({ type: 'triangle', freq: 240, freqEnd: 150, t, dur: 0.5, attack: 0.1, peak: 0.14, filter: LP(800, null, 5), out });
    return 0.5;
  },
  /** El Celador gira, piedra contra piedra. */
  wardenTurn(t, out) {
    noise({ t, dur: 0.6, attack: 0.08, peak: 0.26, filter: LP(420, 1200), out });
    tone({ freq: 58, freqEnd: 44, t, dur: 0.6, peak: 0.3, out });
    return 0.65;
  },
  /** Grito de La Escucha: alarido que llena la sala. */
  shout(t, out) {
    for (const [f, peak] of [[620, 0.12], [940, 0.09], [1420, 0.06]]) tone({ type: 'sawtooth', freq: f, freqEnd: f * 1.25, t, dur: 0.75, attack: 0.05, peak, filter: LP(4200), detune: rand(-8, 8), out });
    noise({ t, dur: 0.7, attack: 0.04, peak: 0.14, filter: BP(2400, null, 2), out });
    return 0.9;
  },
  /** Préstamo: una moneda que cae en la mano equivocada. */
  loan(t, out) {
    bell({ freq: 2400, t, dur: 0.3, peak: 0.08, metal: true, out });
    bell({ freq: 1600, t: t + 0.11, dur: 0.4, peak: 0.08, metal: true, out });
    tone({ freq: 90, freqEnd: 70, t: t + 0.11, dur: 0.2, peak: 0.2, out });
    return 0.5;
  },
  /** Cobro: cascada de monedas hacia la Usurera. */
  collect(t, out, { amount = 1 } = {}) {
    const n = Math.min(10, Math.max(3, 3 + amount * 2));
    for (let i = 0; i < n; i++) bell({ freq: rand(2200, 3800), t: t + i * 0.05, dur: 0.18, peak: 0.05, metal: true, out });
    return 0.2 + n * 0.05;
  },
  /** Ascuas que caen al suelo y rebotan. */
  embersDrop(t, out, { amount = 1 } = {}) {
    const n = Math.min(9, Math.max(3, amount * 3));
    for (let i = 0; i < n; i++) tone({ freq: rand(1400, 2600), t: t + i * 0.055 + i * i * 0.004, dur: 0.05, peak: 0.08 / (1 + i * 0.25), out });
    crackles(t, out, { count: 4, span: 0.3, peak: 0.12 });
    return 0.6;
  },
  /** Recoger ascuas del suelo. */
  embersPickup(t, out) {
    for (let i = 0; i < 3; i++) tone({ type: 'triangle', freq: 880 * 1.26 ** i, t: t + i * 0.055, dur: 0.1, peak: 0.08, out });
    return 0.3;
  },
  /** Soborno: moneda lanzada y un gruñido que se calla. */
  bribe(t, out) {
    bell({ freq: 2800, t, dur: 0.25, peak: 0.07, metal: true, out });
    tone({ type: 'sawtooth', freq: 150, freqEnd: 70, t: t + 0.1, dur: 0.3, peak: 0.1, filter: LP(600), out });
    return 0.45;
  },
  /** Corrimiento: toda una línea de piedra se arrastra. */
  roomShift(t, out) {
    noise({ t, dur: 0.75, attack: 0.1, peak: 0.42, filter: LP(520, 1500), out });
    tone({ freq: 52, freqEnd: 40, t, dur: 0.8, peak: 0.42, out });
    for (let i = 0; i < 7; i++) noise({ t: t + 0.08 + i * 0.09, dur: 0.05, peak: 0.1 * rand(0.5, 1), filter: BP(rand(500, 1500), null, 2), out });
    noise({ t: t + 0.74, dur: 0.12, peak: 0.36, filter: LP(600), out });
    return 0.95;
  },
  /** Repique: campana grande, dos tañidos. */
  bellRing(t, out) {
    for (let i = 0; i < 2; i++) {
      bell({ freq: 330, t: t + i * 0.55, dur: 1.7, peak: 0.2, metal: true, out });
      bell({ freq: 660, t: t + i * 0.55, dur: 1, peak: 0.07, out });
      tone({ freq: 90, freqEnd: 70, t: t + i * 0.55, dur: 0.18, peak: 0.25, out });
    }
    return 2.2;
  },
  /** Último en hablar: el Campanero oye a los demás. */
  lastWord(t, out) {
    bell({ freq: 990, t, dur: 0.5, peak: 0.05, out });
    bell({ freq: 1320, t: t + 0.1, dur: 0.6, peak: 0.04, out });
    return 0.7;
  },
  /** Señal rápida: pitido distinto si es un aviso de peligro. */
  signal(t, out, { danger = false } = {}) {
    const f = danger ? 620 : 1040;
    tone({ type: 'triangle', freq: f, t, dur: 0.08, peak: 0.12, out });
    tone({ type: 'triangle', freq: danger ? f * 0.8 : f * 1.34, t: t + 0.09, dur: 0.1, peak: 0.1, out });
    return 0.22;
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

  // ───────── caminos tras la extinción ─────────
  /** Se abre la elección: un acorde suspendido, mitad campana clara (Amistad) y mitad bordón grave (Venganza). */
  pathOffer(t, out) {
    [81, 86, 88, 93].forEach((n, i) => bell({ freq: midi(n), t: t + 0.15 + i * 0.12, dur: 2.2, peak: 0.045, out }));
    for (const d of [-6, 6]) tone({ type: 'sawtooth', freq: midi(38), t, dur: 2.4, attack: 0.6, peak: 0.05, detune: d, filter: LP(260, 700), out });
    tone({ freq: midi(26), t, dur: 2.2, attack: 0.4, peak: 0.18, out });
    noise({ t, dur: 1.8, attack: 1, peak: 0.05, filter: BP(900, 3500, 0.7), curve: 'lin', out });
    return 2.6;
  },
  /** Amistad: aire que sube y campanas en mayor. */
  pathFriendship(t, out) {
    noise({ t, dur: 0.8, attack: 0.5, peak: 0.1, filter: BP(600, 5000, 0.9), curve: 'lin', out });
    [74, 78, 81, 86, 90].forEach((n, i) => bell({ freq: midi(n), t: t + 0.25 + i * 0.08, dur: 1.6, peak: 0.05, out }));
    for (const n of [62, 69, 74]) tone({ type: 'triangle', freq: midi(n), t: t + 0.2, dur: 1.8, attack: 0.3, peak: 0.04, out });
    return 2;
  },
  /** Venganza: un rugido grave y un metal disonante. */
  pathVengeance(t, out) {
    for (const [f, d] of [[55, -9], [58.3, 9]]) tone({ type: 'sawtooth', freq: f, freqEnd: f * 0.8, t, dur: 1.6, attack: 0.08, peak: 0.12, detune: d, filter: LP(900, 220, 3), out });
    tone({ freq: 70, freqEnd: 32, t, dur: 0.9, peak: 0.45, out });
    bell({ freq: 116.5, t: t + 0.05, dur: 2.4, peak: 0.12, metal: true, out });
    noise({ t, dur: 1, attack: 0.05, peak: 0.16, filter: LP(1400, 180), out });
    return 2.4;
  },
  /** Deriva: soplo que pasa. */
  emberDrift(t, out) {
    noise({ t, dur: 0.45, attack: 0.18, peak: 0.16, filter: BP(900, 2600, 1.4), curve: 'lin', out });
    tone({ freq: midi(84), freqEnd: midi(91), t, dur: 0.4, attack: 0.1, peak: 0.025, out });
    return 0.5;
  },
  /** Avivar: brillo cristalino que se cierra sobre el aliado. */
  emberKindle(t, out) {
    [88, 93, 96].forEach((n, i) => bell({ freq: midi(n), t: t + i * 0.05, dur: 0.9, peak: 0.05, out }));
    tone({ freq: midi(76), freqEnd: midi(83), t, dur: 0.35, attack: 0.05, peak: 0.06, out });
    noise({ t, dur: 0.3, attack: 0.1, peak: 0.05, filter: HP(5000), out });
    return 1;
  },
  /** Estorbo: el tiempo se estira (barrido hacia abajo con trémolo) y un tic de reloj. */
  emberHinder(t, out) {
    tone({ type: 'triangle', freq: midi(79), freqEnd: midi(67), t, dur: 0.6, attack: 0.02, peak: 0.08, vibrato: { rate: 9, depth: 18 }, filter: LP(2200), out });
    for (const dt of [0.05, 0.3]) noise({ t: t + dt, dur: 0.02, peak: 0.25, filter: BP(3200, null, 6), out });
    bell({ freq: midi(70), t: t + 0.3, dur: 0.8, peak: 0.04, metal: true, out });
    return 1;
  },
  /** Faro: una luz que se abre (acorde que crece). */
  emberBeacon(t, out) {
    for (const n of [69, 76, 81]) tone({ freq: midi(n), t, dur: 1.2, attack: 0.35, peak: 0.035, out });
    bell({ freq: midi(93), t: t + 0.3, dur: 1.2, peak: 0.035, out });
    noise({ t, dur: 0.8, attack: 0.4, peak: 0.04, filter: HP(4500), curve: 'lin', out });
    return 1.3;
  },
  /** Calidez: zumbido tibio. */
  emberWarmth(t, out) {
    for (const n of [57, 64]) tone({ type: 'triangle', freq: midi(n), t, dur: 0.9, attack: 0.25, peak: 0.05, filter: LP(1600), out });
    crackles(t + 0.1, out, { count: 5, span: 0.6, peak: 0.12, freq: 2500 });
    return 1;
  },
  /** Eco ganado: un «ping» que sube más cuantos más ecos. */
  echoGained(t, out, { amount = 1 } = {}) {
    const n = Math.min(3, Math.max(1, amount));
    for (let i = 0; i < n; i++) {
      bell({ freq: midi(91 + i * 3), t: t + i * 0.09, dur: 0.9, peak: 0.05, out });
      tone({ freq: midi(79 + i * 3), t: t + i * 0.09, dur: 0.3, attack: 0.01, peak: 0.03, out });
    }
    return 1 + n * 0.09;
  },
  /** Renacer: la llama azul se vuelve fuego; acorde que crece y estalla en campanas. */
  emberReborn(t, out) {
    noise({ t, dur: 1.2, attack: 0.9, peak: 0.14, filter: BP(300, 4200, 0.8), curve: 'lin', out });
    for (const n of [50, 57, 62, 66]) tone({ type: 'triangle', freq: midi(n), t: t + 0.1, dur: 2.4, attack: 0.8, peak: 0.05, out });
    tone({ freq: 90, freqEnd: 45, t: t + 0.95, dur: 0.6, peak: 0.4, out });
    [86, 90, 93, 98].forEach((n, i) => bell({ freq: midi(n), t: t + 1 + i * 0.07, dur: 1.4, peak: 0.05, out }));
    crackles(t + 1, out, { count: 10, span: 0.8, peak: 0.2 });
    return 2.6;
  },
  /** Aparece un Renegado: golpe de bronce bajo, desgarro y bordón. */
  renegadeSpawned(t, out) {
    noise({ t, dur: 0.7, attack: 0.6, peak: 0.16, filter: LP(400, 2400), curve: 'lin', out });
    for (const n of [38, 39, 45]) brass(midi(n), t + 0.6, 1.4, 0.05, out);
    tone({ freq: 60, freqEnd: 30, t: t + 0.6, dur: 1, peak: 0.5, out });
    bell({ freq: 92.5, t: t + 0.62, dur: 3, peak: 0.16, metal: true, out });
    return 3.2;
  },
  /** Cae un Renegado: algo que se quiebra y se apaga. */
  renegadeDefeated(t, out) {
    noise({ t, dur: 0.06, peak: 0.5, filter: HP(2500), out });
    [93, 89, 84, 79, 74].forEach((n, i) => bell({ freq: midi(n), t: t + 0.02 + i * 0.06, dur: 0.7, peak: 0.05, metal: true, out }));
    tone({ type: 'sawtooth', freq: 180, freqEnd: 50, t, dur: 0.9, peak: 0.1, filter: LP(1200, 200), out });
    return 1.2;
  },
  /** Escudo de aviso: cristal que se raja. */
  warningShield(t, out) {
    noise({ t, dur: 0.05, peak: 0.45, filter: HP(4500), out });
    bell({ freq: 1320, t, dur: 0.5, peak: 0.12, metal: true, out });
    bell({ freq: 1870, t: t + 0.01, dur: 0.35, peak: 0.06, metal: true, out });
    crackles(t + 0.02, out, { count: 6, span: 0.15, peak: 0.2, freq: 5000 });
    return 0.6;
  },
  /** «El Umbral reclama»: la derrota del grupo, más grave y con un bordón que no resuelve. */
  threshold(t, out) {
    const chords = [[45, 48, 52], [44, 47, 51], [41, 45, 48], [40, 46, 49]];
    chords.forEach((c, i) => {
      const at = t + i * 1;
      for (const n of c) tone({ type: 'sawtooth', freq: midi(n), t: at, dur: i === 3 ? 4 : 1.4, attack: 0.2, peak: 0.04, filter: LP(900, 400), out });
    });
    for (const dt of [0, 2, 4]) bell({ freq: midi(26), t: t + dt, dur: 3.5, peak: 0.2, metal: true, out });
    return 7.5;
  },
};
