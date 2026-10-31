// Motor de audio (Web Audio API): contexto, buses de mezcla y primitivas de síntesis. Todo es procedural:
// la CSP no permite media-src y el cliente no descarga recursos, así que no hay archivos de audio.
//
// Grafo:  voces SFX ─┬─► sfxVol ─► compresor SFX ─┐
//                    └─► sfxWetVol ─► reverb ─────┤
//         temas  ────┬─► musicVol ─► filtro música ┼─► master (mute) ─► limitador ─► salida
//                    └─► musicWetVol ─► reverb ───┘

const AC = globalThis.AudioContext ?? globalThis.webkitAudioContext;

export const engine = {
  ctx: null,
  master: null,
  sfxVol: null,
  sfxWetVol: null,
  musicVol: null,
  musicWetVol: null,
  musicFilter: null,
  noise: null,
  failed: typeof AC !== 'function',
  muted: false,
};

/** Crea el contexto la primera vez (debe ocurrir dentro de un gesto del usuario) y lo reanuda si estaba suspendido. */
export function ensureContext() {
  if (engine.failed) return null;
  if (!engine.ctx) {
    try {
      build();
    } catch (err) {
      engine.failed = true;
      console.warn('audio: Web Audio no disponible', err);
      return null;
    }
  }
  if (engine.ctx.state === 'suspended' && !engine.muted) engine.ctx.resume().catch(() => {});
  return engine.ctx;
}

/** El contexto si está sonando; si no (sin gesto aún, silenciado, sin soporte), null. */
export function liveContext() {
  const { ctx } = engine;
  return ctx && ctx.state === 'running' && !engine.muted ? ctx : null;
}

function build() {
  const ctx = new AC({ latencyHint: 'interactive' });
  const limiter = compressor(ctx, { threshold: -3, knee: 0, ratio: 20, attack: 0.002, release: 0.2 });
  limiter.connect(ctx.destination);
  const master = gainNode(ctx, 0);
  master.connect(limiter);

  const reverb = ctx.createConvolver();
  reverb.buffer = impulse(ctx, 3.4, 2.8);
  reverb.connect(master);

  const sfxGlue = compressor(ctx, { threshold: -16, knee: 6, ratio: 3, attack: 0.004, release: 0.15 });
  sfxGlue.connect(master);
  const sfxVol = gainNode(ctx, 0);
  sfxVol.connect(sfxGlue);
  const sfxWetVol = gainNode(ctx, 0);
  sfxWetVol.connect(reverb);

  const musicFilter = ctx.createBiquadFilter();
  musicFilter.type = 'lowpass';
  musicFilter.frequency.value = 18000;
  musicFilter.connect(master);
  const musicVol = gainNode(ctx, 0);
  musicVol.connect(musicFilter);
  const musicWetVol = gainNode(ctx, 0);
  musicWetVol.connect(reverb);

  const noise = ctx.createBuffer(1, ctx.sampleRate * 2, ctx.sampleRate);
  const data = noise.getChannelData(0);
  for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;

  Object.assign(engine, { ctx, master, sfxVol, sfxWetVol, musicVol, musicWetVol, musicFilter, noise });
}

/** Respuesta al impulso de una sala de piedra: ruido con caída exponencial y algunos ecos tempranos. */
function impulse(ctx, seconds, decay) {
  const len = Math.floor(ctx.sampleRate * seconds);
  const buf = ctx.createBuffer(2, len, ctx.sampleRate);
  for (let ch = 0; ch < 2; ch++) {
    const d = buf.getChannelData(ch);
    for (let i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * (1 - i / len) ** decay;
    for (const ms of [17, 31, 47, 73, 97]) {
      const at = Math.floor(((ms + ch * 5) / 1000) * ctx.sampleRate);
      d[at] += (Math.random() < 0.5 ? -1 : 1) * 0.6;
    }
  }
  return buf;
}

function compressor(ctx, p) {
  const c = ctx.createDynamicsCompressor();
  c.threshold.value = p.threshold;
  c.knee.value = p.knee;
  c.ratio.value = p.ratio;
  c.attack.value = p.attack;
  c.release.value = p.release;
  return c;
}

export function gainNode(ctx, value = 1) {
  const g = ctx.createGain();
  g.gain.value = value;
  return g;
}

/** Lleva un AudioParam a un valor en `seconds` sin chasquidos. */
export function rampTo(param, value, seconds = 0.08) {
  const { ctx } = engine;
  if (!ctx) return;
  const t = ctx.currentTime;
  param.cancelScheduledValues(t);
  param.setValueAtTime(param.value, t);
  param.linearRampToValueAtTime(value, t + seconds);
}

/** Desconecta un nodo cuando ya no suena (las fuentes se detienen solas; esto libera los nodos intermedios). */
export function releaseLater(node, seconds) {
  setTimeout(() => { try { node.disconnect(); } catch { /* ya desconectado */ } }, Math.max(0, seconds) * 1000 + 200);
}

export const midi = (n) => 440 * 2 ** ((n - 69) / 12);
export const rand = (a, b) => a + Math.random() * (b - a);
export const pick = (list) => list[Math.floor(Math.random() * list.length)];
export const clamp = (v, a, b) => Math.min(b, Math.max(a, v));

/** Salida de una voz SFX con paneo y envío a la reverb. Devuelve el nodo al que conectar las fuentes. */
export function sfxOut({ pan = 0, gain = 1, wet = 0.15 } = {}) {
  const { ctx } = engine;
  const out = gainNode(ctx, gain);
  let tail = out;
  if (pan && ctx.createStereoPanner) {
    const p = ctx.createStereoPanner();
    p.pan.value = clamp(pan, -1, 1);
    out.connect(p);
    tail = p;
  }
  tail.connect(engine.sfxVol);
  if (wet > 0) {
    const send = gainNode(ctx, wet);
    tail.connect(send);
    send.connect(engine.sfxWetVol);
    out.extra = [tail, send];
  } else {
    out.extra = [tail];
  }
  return out;
}

export function releaseOut(out, seconds) {
  releaseLater(out, seconds);
  for (const n of out.extra ?? []) releaseLater(n, seconds);
}

function envelope(g, t, attack, peak, dur, curve = 'exp') {
  const p = g.gain;
  p.setValueAtTime(0.0001, t);
  p.linearRampToValueAtTime(peak, t + attack);
  if (curve === 'lin') p.linearRampToValueAtTime(0.0001, t + dur);
  else p.exponentialRampToValueAtTime(0.0001, t + dur);
}

function applyFilter(ctx, f, t, dur, src) {
  const bq = ctx.createBiquadFilter();
  bq.type = f.type ?? 'lowpass';
  bq.Q.value = f.q ?? 0.7;
  bq.frequency.setValueAtTime(f.freq, t);
  if (f.to) bq.frequency.exponentialRampToValueAtTime(f.to, t + (f.time ?? dur));
  if (f.back) bq.frequency.exponentialRampToValueAtTime(f.back, t + dur);
  src.connect(bq);
  return bq;
}

/**
 * Oscilador con envolvente. freqEnd hace un barrido; filter = { type, freq, to?, time?, back?, q? };
 * vibrato = { rate, depth } (en Hz de desviación).
 */
export function tone({ type = 'sine', freq, freqEnd, glide, t, dur, attack = 0.005, peak = 0.3, detune = 0, filter, vibrato, curve, out }) {
  const { ctx } = engine;
  const osc = ctx.createOscillator();
  osc.type = type;
  osc.frequency.setValueAtTime(freq, t);
  if (freqEnd) osc.frequency.exponentialRampToValueAtTime(freqEnd, t + (glide ?? dur));
  if (detune) osc.detune.value = detune;
  let lfo = null;
  if (vibrato) {
    lfo = ctx.createOscillator();
    lfo.frequency.value = vibrato.rate;
    const depth = gainNode(ctx, vibrato.depth);
    lfo.connect(depth);
    depth.connect(osc.frequency);
    lfo.start(t);
    lfo.stop(t + dur + 0.05);
  }
  const g = ctx.createGain();
  envelope(g, t, attack, peak, dur, curve);
  const src = filter ? applyFilter(ctx, filter, t, dur, osc) : osc;
  src.connect(g);
  g.connect(out);
  osc.start(t);
  osc.stop(t + dur + 0.05);
  osc.onended = () => { g.disconnect(); if (src !== osc) src.disconnect(); };
  return osc;
}

/** Ráfaga de ruido filtrado con envolvente. */
export function noise({ t, dur, attack = 0.003, peak = 0.3, filter = { type: 'lowpass', freq: 2000 }, curve, rate = 1, out }) {
  const { ctx } = engine;
  const src = ctx.createBufferSource();
  src.buffer = engine.noise;
  src.loop = true;
  src.playbackRate.value = rate;
  const g = ctx.createGain();
  envelope(g, t, attack, peak, dur, curve);
  const f = applyFilter(ctx, filter, t, dur, src);
  f.connect(g);
  g.connect(out);
  src.start(t, Math.random() * 1.5);
  src.stop(t + dur + 0.05);
  src.onended = () => { g.disconnect(); f.disconnect(); };
  return src;
}

const BELL = [1, 2.0, 2.76, 4.07, 5.4, 6.8];
const METAL = [1, 1.48, 2.03, 2.71, 3.6, 4.4];

/** Campana por síntesis aditiva de parciales inarmónicos (los agudos se apagan antes). */
export function bell({ freq, t, dur = 2, peak = 0.1, out, metal = false, partials }) {
  const ratios = partials ?? (metal ? METAL : BELL);
  ratios.forEach((r, i) => {
    const f = freq * r;
    if (f > 16000) return;
    tone({ type: 'sine', freq: f, t, dur: dur / (1 + i * 0.6), attack: 0.002, peak: peak / (1 + i * 0.8), detune: rand(-4, 4), out });
  });
}
