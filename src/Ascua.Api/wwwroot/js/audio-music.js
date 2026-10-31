// Música generativa: nada está grabado ni en bucle. Cada tema elige acordes con una cadena de Márkov en Re
// menor (eólico/frigio), compone arpegios, frases y percusión al vuelo y se programa con unos segundos de
// anticipación sobre el reloj del AudioContext. Los cambios de tema se funden (crossfade).
//   ambient  lobby e inicio: pad oscuro, drones, viento y campanas lejanas, sin pulso.
//   explore  partida: pulso lento con arpegios; la intensidad (enemigos a la vista, ataques anunciados
//            sobre el jugador) suma percusión, cuerdas en trémolo y abre el filtro.
//   boss     el Celador a la vista: ostinato grave, tambores de guerra, coro y metales.
import { bell, clamp, engine, gainNode, midi, noise, pick, rampTo, rand, releaseLater, tone } from './audio-engine.js';

const LOOKAHEAD = 2.2;
const TICK_MS = 250;
const FADE = 3.5;

// Acordes en semitonos desde Re.
const CHORDS = {
  i: [0, 3, 7], iv: [5, 8, 12], v: [7, 10, 14], V: [7, 11, 14], III: [3, 7, 10],
  VI: [-4, 0, 3], VII: [-2, 2, 5], bII: [1, 5, 8],
};
const CALM = {
  i: ['VI', 'iv', 'VII', 'III', 'v', 'VI'], VI: ['VII', 'iv', 'i', 'III'], VII: ['i', 'III', 'VI'],
  III: ['VI', 'VII', 'iv'], iv: ['i', 'v', 'VI', 'VII'], v: ['i', 'VI'],
};
const DARK = {
  i: ['bII', 'VII', 'iv', 'bII', 'VI'], bII: ['i', 'VII', 'i'], VII: ['i', 'bII', 'VI'],
  VI: ['bII', 'V', 'VII'], iv: ['V', 'bII', 'i'], V: ['i'],
};
const AEOLIAN = [0, 2, 3, 5, 7, 8, 10];

const LP = (freq, to, q) => ({ type: 'lowpass', freq, to, q });

/** Nota sostenida (ataque, meseta, cierre) con varios osciladores desafinados y filtros en paralelo. */
function sustained({ type = 'sawtooth', freq, t, dur, attack, release, peak, detunes = [0], filters = [LP(900)], out }) {
  const { ctx } = engine;
  attack = Math.min(attack, dur * 0.45);
  release = Math.min(release, dur - attack);
  const g = ctx.createGain();
  g.gain.setValueAtTime(0, t);
  g.gain.linearRampToValueAtTime(peak, t + attack);
  g.gain.setValueAtTime(peak, t + dur - release);
  g.gain.linearRampToValueAtTime(0, t + dur);
  g.connect(out);
  const bqs = filters.map((f) => {
    const bq = ctx.createBiquadFilter();
    bq.type = f.type;
    bq.frequency.value = f.freq;
    bq.Q.value = f.q ?? 0.7;
    bq.connect(g);
    return bq;
  });
  let last = null;
  for (const d of detunes) {
    const osc = ctx.createOscillator();
    osc.type = type;
    osc.frequency.value = freq;
    osc.detune.value = d;
    for (const bq of bqs) osc.connect(bq);
    osc.start(t);
    osc.stop(t + dur + 0.1);
    last = osc;
  }
  last.onended = () => { g.disconnect(); bqs.forEach((b) => b.disconnect()); };
  return g;
}

/** Ganancia con trémolo (cuerdas nerviosas). */
function tremolo(t, dur, rate, out) {
  const { ctx } = engine;
  const trem = gainNode(ctx, 0.5);
  const lfo = ctx.createOscillator();
  lfo.frequency.value = rate;
  const depth = gainNode(ctx, 0.5);
  lfo.connect(depth);
  depth.connect(trem.gain);
  trem.connect(out);
  lfo.start(t);
  lfo.stop(t + dur + 0.2);
  lfo.onended = () => { trem.disconnect(); depth.disconnect(); };
  return trem;
}

class Theme {
  constructor(kind, stepDur) {
    const { ctx } = engine;
    this.kind = kind;
    this.stepDur = stepDur;
    this.n = 0;
    this.next = 0;
    this.chordName = 'i';
    this.chord = CHORDS.i;
    this.intensity = 0;
    this.target = 0;
    this.danger = false;
    this.loops = [];
    this.fade = gainNode(ctx, 0);
    this.fade.connect(engine.musicVol);
    this.wetFade = gainNode(ctx, 0);
    this.wetFade.connect(engine.musicWetVol);
    // near: cerca, con algo de sala; far: casi todo reverb (campanas lejanas, frases).
    this.near = gainNode(ctx, 1);
    this.near.connect(this.fade);
    this.nearSend = gainNode(ctx, 0.3);
    this.near.connect(this.nearSend);
    this.nearSend.connect(this.wetFade);
    this.far = gainNode(ctx, 1);
    this.farDry = gainNode(ctx, 0.22);
    this.far.connect(this.farDry);
    this.farDry.connect(this.fade);
    this.far.connect(this.wetFade);
  }

  start(t, seconds) {
    this.next = t;
    for (const p of [this.fade.gain, this.wetFade.gain]) {
      p.setValueAtTime(0, engine.ctx.currentTime);
      p.linearRampToValueAtTime(1, t + seconds);
    }
    this.startLoops(t);
  }

  stop(seconds) {
    const end = engine.ctx.currentTime + seconds;
    rampTo(this.fade.gain, 0, seconds);
    rampTo(this.wetFade.gain, 0, seconds);
    for (const src of this.loops) { try { src.stop(end + 0.1); } catch { /* ya detenida */ } }
    const later = seconds + LOOKAHEAD + 8;
    for (const node of [this.fade, this.wetFade, this.near, this.nearSend, this.far, this.farDry]) releaseLater(node, later);
  }

  startLoops() {}

  /** Viento: ruido en bucle con un filtro de banda que deriva lentamente. */
  wind(t, level) {
    const { ctx } = engine;
    const src = ctx.createBufferSource();
    src.buffer = engine.noise;
    src.loop = true;
    const bq = ctx.createBiquadFilter();
    bq.type = 'bandpass';
    bq.frequency.value = 500;
    bq.Q.value = 2.5;
    const lfo = ctx.createOscillator();
    lfo.frequency.value = rand(0.03, 0.07);
    const depth = gainNode(ctx, 320);
    lfo.connect(depth);
    depth.connect(bq.frequency);
    const g = gainNode(ctx, level);
    src.connect(bq);
    bq.connect(g);
    g.connect(this.far);
    src.start(t);
    lfo.start(t);
    this.loops.push(src, lfo);
  }

  setIntensity(target, danger) {
    this.target = clamp(target, 0, 1);
    this.danger = danger;
  }

  schedule(now, until) {
    // Tras una suspensión (silencio, pestaña congelada) no se recupera lo perdido: se sigue desde ahora.
    if (this.next < now) this.next = now + 0.05;
    while (this.next < until) {
      this.intensity += (this.target - this.intensity) * 0.06;
      this.step(this.next, this.n);
      this.n++;
      this.next += this.stepDur;
    }
  }

  nextChord(graph) {
    this.chordName = pick(graph[this.chordName] ?? ['i']);
    this.chord = CHORDS[this.chordName];
    return this.chord;
  }

  /** Notas de la escala cercanas a la raíz del acorde, para frases melódicas. */
  phrase(t, base, notes, noteDur, peak, out) {
    let deg = AEOLIAN.indexOf(((this.chord[0] % 12) + 12) % 12);
    if (deg < 0) deg = 0;
    let at = t;
    for (let i = 0; i < notes; i++) {
      deg = clamp(deg + pick([-2, -1, -1, 1, 1, 2, 0]), 0, AEOLIAN.length + 3);
      const semis = AEOLIAN[deg % AEOLIAN.length] + 12 * Math.floor(deg / AEOLIAN.length);
      const len = noteDur * pick([1, 1, 1.5, 2]);
      tone({ freq: midi(base + semis), t: at, dur: len * 1.6, attack: 0.08, peak, vibrato: { rate: 5, depth: 2.5 }, out });
      at += len;
    }
  }
}

class Ambient extends Theme {
  constructor() {
    super('ambient', 0.5);
    this.chordEnd = 0;
    this.nextBell = 0;
    this.nextBoom = 0;
    this.nextPhrase = 0;
  }

  startLoops(t) {
    this.wind(t, 0.05);
    this.chordEnd = t;
    this.nextBell = t + rand(2, 4);
    this.nextBoom = t + rand(8, 16);
    this.nextPhrase = t + rand(14, 24);
  }

  step(t) {
    if (t >= this.chordEnd) {
      const chord = this.n === 0 ? this.chord : this.nextChord(CALM);
      const len = rand(11, 17);
      const notes = chord.map((s) => 50 + s);
      if (Math.random() < 0.45) notes.push(50 + chord[0] + 14);
      for (const n of notes) {
        sustained({ freq: midi(n), t, dur: len + 4, attack: 3.5, release: 4, peak: 0.018, detunes: [-9, 7], filters: [LP(rand(500, 800))], out: this.near });
      }
      const root = 38 + chord[0];
      sustained({ type: 'sine', freq: midi(root), t, dur: len + 4, attack: 3, release: 4, peak: 0.07, out: this.near });
      sustained({ type: 'sawtooth', freq: midi(root - 12), t, dur: len + 4, attack: 4, release: 4, peak: 0.03, filters: [LP(160)], detunes: [-5, 5], out: this.near });
      this.chordEnd = t + len;
    }
    if (t >= this.nextBell) {
      const n = 74 + pick(this.chord) + pick([0, 0, 12]);
      bell({ freq: midi(n), t, dur: rand(3, 5), peak: rand(0.025, 0.045), out: this.far });
      this.nextBell = t + rand(2.5, 8);
    }
    if (t >= this.nextBoom) {
      tone({ freq: 52, freqEnd: 34, t, dur: 2.2, attack: 0.02, peak: 0.12, out: this.far });
      this.nextBoom = t + rand(18, 36);
    }
    if (t >= this.nextPhrase) {
      this.phrase(t, 62, Math.floor(rand(3, 6)), 1.4, 0.022, this.far);
      this.nextPhrase = t + rand(20, 40);
    }
  }
}

class Explore extends Theme {
  constructor() {
    super('explore', 60 / 74 / 2);
    this.barsPerChord = 2;
    this.chordBar = 0;
    this.arpIdx = 2;
    this.phraseUntil = 0;
  }

  startLoops(t) {
    this.wind(t, 0.03);
  }

  step(t, n) {
    const s = n % 8;
    const bar = Math.floor(n / 8);
    const lvl = this.intensity;
    const barDur = this.stepDur * 8;
    if (s === 0 && bar >= this.chordBar) {
      this.barsPerChord = pick([2, 2, 4]);
      this.chordBar = bar + this.barsPerChord;
      const chord = n === 0 ? this.chord : this.nextChord(lvl > 0.7 ? DARK : CALM);
      const len = barDur * this.barsPerChord;
      const cutoff = 450 + lvl * 1100;
      for (const x of chord) sustained({ freq: midi(50 + x), t, dur: len + 1.5, attack: 1.2, release: 1.5, peak: 0.016, detunes: [-8, 8], filters: [LP(cutoff)], out: this.near });
      sustained({ type: 'triangle', freq: midi(38 + chord[0]), t, dur: len + 1, attack: 0.3, release: 1.2, peak: 0.09, filters: [LP(300)], out: this.near });
      if (lvl > 0.45) {
        const trem = tremolo(t, len, 6 + lvl * 4, this.near);
        for (const x of [chord[1] + 72, chord[2] + 72]) {
          sustained({ freq: midi(x), t, dur: len, attack: 0.8, release: 0.8, peak: (lvl - 0.4) * 0.03, detunes: [-6, 6], filters: [LP(2400)], out: trem });
        }
      }
      if (Math.random() < 0.3) bell({ freq: midi(86 + pick(chord)), t, dur: 3, peak: 0.025, out: this.far });
    }

    // Arpegio: camina por las notas del acorde en dos octavas; más denso y brillante con la intensidad.
    const p = (s % 2 === 0 ? 0.45 : 0.18) + lvl * 0.4;
    if (t >= this.phraseUntil && Math.random() < p) {
      const tones = [...this.chord.map((x) => 62 + x), ...this.chord.map((x) => 74 + x)];
      this.arpIdx = clamp(this.arpIdx + pick([-2, -1, 1, 1, 2]), 0, tones.length - 1);
      const cutoff = 1000 + lvl * 2800;
      tone({ type: 'triangle', freq: midi(tones[this.arpIdx]), t, dur: rand(0.6, 1.2), attack: 0.004, peak: 0.05, filter: LP(cutoff, cutoff * 0.3), out: this.near });
    }
    if (s === 0 && Math.random() < 0.14 && lvl < 0.7) {
      const notes = Math.floor(rand(3, 6));
      this.phrase(t, 74, notes, this.stepDur * 2, 0.022, this.far);
      this.phraseUntil = t + notes * this.stepDur * 2.5;
    }

    // Percusión según la intensidad.
    if (lvl > 0.2 && (s === 0 || (s === 4 && Math.random() < lvl))) this.drum(t, 0.12 + lvl * 0.25);
    if (lvl > 0.55 && (s === 3 || s === 7) && Math.random() < lvl) this.drum(t, 0.08 + lvl * 0.08, 150);
    if (lvl > 0.45 && s % 2 === 1) noise({ t, dur: 0.05, peak: 0.02 + lvl * 0.03, filter: { type: 'highpass', freq: 7000 }, out: this.near });
    if (lvl > 0.75 && s >= 6 && bar === this.chordBar - 1) {
      this.drum(t, 0.2, 130);
      this.drum(t + this.stepDur / 2, 0.16, 115);
    }
    // Latido cuando el jugador tiene poca vida.
    if (this.danger && s === 0) {
      tone({ freq: 62, freqEnd: 40, t, dur: 0.15, peak: 0.3, out: this.near });
      tone({ freq: 58, freqEnd: 38, t: t + 0.22, dur: 0.15, peak: 0.22, out: this.near });
    }
  }

  drum(t, peak, freq = 110) {
    tone({ freq, freqEnd: freq / 2, t, dur: 0.35, peak, out: this.near });
    noise({ t, dur: 0.08, peak: peak * 0.4, filter: LP(500), out: this.near });
  }
}

const OSTINATOS = [
  [1, 0, 0.6, 0, 1, 0.6, 0, 0.6],
  [1, 0.5, 0.5, 1, 0.5, 0.5, 1, 0.5],
  [1, 0, 0, 0.7, 0, 0.7, 1, 0],
  [1, 0.6, 1, 0, 1, 0.6, 0.8, 0.6],
  [1, 0, 0.7, 0.7, 0, 0.7, 0.7, 0],
];

class Boss extends Theme {
  constructor() {
    super('boss', 60 / 100 / 2);
    this.pattern = OSTINATOS[0];
    this.target = 0.6;
    this.intensity = 0.5;
  }

  startLoops(t) {
    this.wind(t, 0.02);
    this.chordName = 'i';
    this.chord = CHORDS.i;
  }

  step(t, n) {
    const s = n % 8;
    const bar = Math.floor(n / 8);
    const lvl = this.intensity;
    const perChord = lvl > 0.8 ? 1 : 2;
    if (s === 0) {
      this.pattern = Math.random() < 0.7 ? this.pattern : pick(OSTINATOS);
      if (Math.random() < 0.25) this.pattern = this.pattern.map((a) => (a === 0 && Math.random() < 0.3 ? 0.5 : a));
      if (bar % perChord === 0) {
        const chord = n === 0 ? this.chord : this.nextChord(DARK);
        const len = this.stepDur * 8 * perChord;
        // Coro: sierras con dos formantes de «a».
        for (const x of chord) {
          sustained({ freq: midi(50 + x), t, dur: len + 0.8, attack: 0.6, release: 0.8, peak: 0.03, detunes: [-12, 0, 11],
            filters: [{ type: 'bandpass', freq: 720, q: 5 }, { type: 'bandpass', freq: 1150, q: 6 }], out: this.near });
        }
        if (n > 0) for (const x of chord) this.brass(midi(62 + x), t, 0.55, 0.03 + lvl * 0.02);
        if (bar % 4 === 0) bell({ freq: midi(38 + chord[0]), t, dur: 4, peak: 0.12, out: this.far });
      }
    }
    // Ostinato grave; al final del compás a veces sube a la quinta o a la segunda menor.
    const accent = this.pattern[s];
    if (accent > 0) {
      let note = 38 + this.chord[0];
      if (s === 7 && Math.random() < 0.35) note += pick([1, 7]);
      tone({ type: 'sawtooth', freq: midi(note), t, dur: 0.26, attack: 0.005, peak: 0.07 * accent, filter: LP(300 + accent * 700 * (0.6 + lvl), 200, 3), out: this.near });
      tone({ type: 'square', freq: midi(note - 12), t, dur: 0.22, peak: 0.04 * accent, filter: LP(180), out: this.near });
    }
    // Tambores de guerra.
    if (s === 0 || s === 4) this.taiko(t, 0.4);
    if ((s === 2 || s === 6) && lvl > 0.5) noise({ t, dur: 0.15, peak: 0.06 + lvl * 0.05, filter: { type: 'bandpass', freq: 1800, q: 0.8 }, out: this.near });
    if (s % 2 === 1 && Math.random() < lvl * 0.5) this.taiko(t, 0.15, 140);
    if (bar % 4 === 3 && s >= 5) {
      this.taiko(t, 0.25, 130);
      this.taiko(t + this.stepDur / 2, 0.22, 120);
    }
  }

  taiko(t, peak, freq = 90) {
    tone({ freq, freqEnd: freq * 0.5, t, dur: 0.5, peak, out: this.near });
    noise({ t, dur: 0.1, peak: peak * 0.5, filter: LP(700), out: this.near });
  }

  brass(freq, t, dur, peak) {
    for (const d of [-8, 8]) {
      tone({ type: 'sawtooth', freq, t, dur, attack: 0.05, peak, detune: d, filter: { type: 'lowpass', freq: 300, to: 2000, time: 0.1, back: 500, q: 1.5 }, out: this.near });
    }
  }
}

const THEMES = { ambient: Ambient, explore: Explore, boss: Boss };

let current = null;
let desired = 'none';
let intensity = { target: 0, danger: false };
let timer = 0;

/** Pide un tema ('ambient' | 'explore' | 'boss' | 'none'). Si aún no hay contexto, se aplicará al crearse. */
export function setTheme(kind) {
  desired = kind in THEMES ? kind : 'none';
  syncMusic();
}

export function currentTheme() { return desired; }

export function setMusicIntensity(target, danger = false) {
  intensity = { target, danger };
  current?.setIntensity(target, danger);
}

/** Lleva el tema que suena al pedido; se llama también cuando el contexto se crea o se reanuda. */
export function syncMusic() {
  const { ctx } = engine;
  if (!ctx) return;
  if ((current?.kind ?? 'none') !== desired) {
    current?.stop(FADE);
    current = null;
    if (desired !== 'none') {
      try {
        current = new THEMES[desired]();
        current.setIntensity(intensity.target, intensity.danger);
        current.start(ctx.currentTime + 0.05, FADE);
      } catch (err) {
        console.warn('audio: no se pudo iniciar la música', err);
        current = null;
      }
    }
  }
  if (!timer) timer = setInterval(tick, TICK_MS);
}

function tick() {
  const { ctx } = engine;
  if (!ctx || ctx.state !== 'running' || !current) return;
  try {
    current.schedule(ctx.currentTime, ctx.currentTime + LOOKAHEAD);
  } catch (err) {
    console.warn('audio: música', err);
    current = null;
    desired = 'none';
  }
}
