// Sonido y música del cliente, 100% procedurales (Web Audio API). Se suscribe al bus (bus.js) y nunca toca
// la lógica de la partida. Si el navegador no tiene Web Audio o bloquea el audio, todo queda en silencio
// sin errores. El AudioContext se crea en el primer gesto del usuario (política de autoplay).
//
// API: initAudio() (una vez, en main.js), previewSfx(kind) para el panel de ajustes al mover un slider
// ('sfx' | 'music') y playSfx(name, opts) para reproducir una receta de audio-sfx.js desde otro módulo.
import { on } from './bus.js';
import { getSettings } from './settings.js';
import { bell, clamp, engine, ensureContext, liveContext, midi, rampTo, releaseOut, sfxOut } from './audio-engine.js';
import { SFX } from './audio-sfx.js';
import { setMusicIntensity, setTheme, syncMusic } from './audio-music.js';

const MAX_VOICES = 18;
const MUSIC_LEVEL = 2.5;
const INTERACTIVE = 'button, a[href], [role="button"], summary, select, input[type="checkbox"], input[type="radio"]';
const MENU_SCREENS = new Set(['auth', 'lobby', 'room', 'queue']);

let started = false;
let configured = false;
let settings = getSettings();
let voices = 0;
const lastAt = new Map();

/** Reproduce una receta; delay en segundos (para escalonar), pan −1..1, gain y envío a reverb (wet). */
export function playSfx(name, { delay = 0, pan = 0, gain = 1, wet = 0.15, opts, minGap = 0.03 } = {}) {
  const ctx = liveContext();
  const recipe = SFX[name];
  if (!ctx || !recipe || voices >= MAX_VOICES) return;
  const t = ctx.currentTime + 0.01 + delay;
  if (t - (lastAt.get(name) ?? -Infinity) < minGap) return;
  lastAt.set(name, t);
  const out = sfxOut({ pan, gain, wet });
  let dur = 1;
  try {
    dur = recipe(t, out, opts) ?? 1;
  } finally {
    voices++;
    setTimeout(() => { voices--; }, (delay + dur) * 1000 + 50);
    releaseOut(out, delay + dur + 0.3);
  }
}

let lastPreview = 0;
/** Sonido de muestra para el panel de ajustes (llamarlo después de updateSettings). */
export function previewSfx(kind = 'sfx') {
  unlock();
  const now = performance.now();
  if (now - lastPreview < 140) return;
  lastPreview = now;
  if (kind === 'music') {
    const ctx = liveContext();
    if (ctx) bell({ freq: midi(62), t: ctx.currentTime + 0.02, dur: 1.6, peak: 0.12, out: engine.musicVol });
  } else {
    playSfx('preview', { minGap: 0 });
  }
}

// ───────────── volumen, silencio, visibilidad ─────────────

function applyVolumes(seconds = 0.15) {
  const { ctx } = engine;
  if (!ctx) return;
  const sfx = clamp(Number(settings.sfxVolume) || 0, 0, 1) ** 2;
  // La música se mezcla más baja que los efectos; MUSIC_LEVEL la deja de fondo pero presente.
  const music = clamp(Number(settings.musicVolume) || 0, 0, 1) ** 2 * MUSIC_LEVEL * (document.hidden ? 0.2 : 1);
  rampTo(engine.sfxVol.gain, sfx, seconds);
  rampTo(engine.sfxWetVol.gain, sfx, seconds);
  rampTo(engine.musicVol.gain, music, document.hidden ? 1.2 : seconds);
  rampTo(engine.musicWetVol.gain, music, document.hidden ? 1.2 : seconds);

  if (settings.muted) {
    engine.muted = true;
    rampTo(engine.master.gain, 0, 0.2);
    // Suspendido no gasta CPU; la música se retoma desde donde vaya al quitar el silencio.
    setTimeout(() => { if (engine.muted && ctx.state === 'running') ctx.suspend().catch(() => {}); }, 300);
  } else if (engine.muted || ctx.state !== 'running') {
    engine.muted = false;
    ctx.resume().then(() => { rampTo(engine.master.gain, 1, 0.4); syncMusic(); }).catch(() => {});
  } else {
    rampTo(engine.master.gain, 1, 0.4);
  }
}

/**
 * Pestaña o ventana oculta (minimizada, otra pestaña, panel cerrado): nada debe seguir sonando.
 * Se funde a silencio y se suspende el AudioContext; al volver se reanuda donde iba (salvo silencio).
 */
function onVisibility() {
  const { ctx } = engine;
  if (!ctx) return;
  if (document.hidden) {
    rampTo(engine.master.gain, 0, 0.15);
    setTimeout(() => { if (document.hidden && ctx.state === 'running') ctx.suspend().catch(() => {}); }, 200);
  } else {
    applyVolumes();
  }
}

function shutdown() {
  const { ctx } = engine;
  if (!ctx || ctx.state === 'closed') return;
  try { engine.master.gain.value = 0; } catch { /* ya cerrado */ }
  ctx.close().catch(() => {});
}

function unlock() {
  const ctx = ensureContext();
  if (!ctx) return;
  if (!configured) {
    configured = true;
    ctx.onstatechange = () => { if (ctx.state === 'running') syncMusic(); };
    applyVolumes(0.5);
  }
  syncMusic();
}

// ───────────── interfaz: hover y clic delegados ─────────────

let hovered = null;

function bindInterface() {
  document.addEventListener('pointerover', (e) => {
    if (e.pointerType && e.pointerType !== 'mouse') return;
    const target = e.target.closest?.(INTERACTIVE);
    if (!target || target === hovered) return;
    hovered = target;
    if (!target.disabled) playSfx('uiHover', { minGap: 0.05, wet: 0.05, gain: 0.8 });
  });
  document.addEventListener('pointerout', (e) => {
    if (hovered && !hovered.contains(e.relatedTarget)) hovered = null;
  });
  document.addEventListener('click', (e) => {
    const target = e.target.closest?.(INTERACTIVE);
    if (target && !target.disabled) playSfx('uiClick', { wet: 0.05 });
  }, true);
}

// ───────────── partida ─────────────

let screen = '';
let bossSeen = false;
let decided = false;
let floorPending = false;
let finishTimer = 0;
let tickTimers = [];

function clearTicks() {
  tickTimers.forEach(clearTimeout);
  tickTimers = [];
}

/** Tics en cada segundo que falta (4, 3, 2, 1), cada vez más agudos. */
function scheduleTicks(leftMs) {
  clearTicks();
  for (let s = Math.floor(leftMs / 1000); s >= 1; s--) {
    tickTimers.push(setTimeout(() => {
      playSfx('tick', { opts: { urgency: 5 - s }, gain: decided ? 0.45 : 1, wet: 0.08, minGap: 0 });
    }, leftMs - s * 1000));
  }
}

function matchTheme() {
  return bossSeen ? 'boss' : 'explore';
}

function onScreen({ name }) {
  screen = name;
  clearTimeout(finishTimer);
  if (MENU_SCREENS.has(name)) setTheme('ambient');
  else if (name === 'match') setTheme(matchTheme());
  else if (name === 'finished') setTheme('none');
}

function onFinished({ iWon, outcome }) {
  clearTicks();
  setTheme('none');
  const teamWon = outcome === 'WARDEN_DEFEATED' || outcome === 'DRAW';
  let length = 7.5;
  if (iWon) { playSfx('victory', { wet: 0.3, opts: { minor: outcome === 'THRESHOLD_CLAIMED' } }); length = 4.5; } else if (teamWon) { playSfx('victory', { wet: 0.3, opts: { minor: true } }); length = 4.5; } else if (outcome === 'THRESHOLD_CLAIMED') playSfx('threshold', { wet: 0.45 }); else playSfx('defeat', { wet: 0.4 });
  clearTimeout(finishTimer);
  finishTimer = setTimeout(() => { if (screen === 'finished') setTheme('ambient'); }, (length + 1) * 1000);
}

const HERO_ATTACK = { YUNQUE: 'attackYunque', CHISPA: 'attackChispa', CUERVO: 'attackCuervo', RAIZ: 'attackRaiz' };

function lookup(id, before, after) {
  if (!id) return null;
  if (String(id).startsWith('enemy:')) {
    const enemyId = Number(String(id).slice(6));
    const enemy = after.enemies.find((e) => e.enemyId === enemyId) ?? before.enemies.find((e) => e.enemyId === enemyId);
    return enemy ? { kind: enemy.kind, position: enemy.position, enemy: true } : { enemy: true };
  }
  for (const side of [after, before]) {
    if (side.me?.playerId === id) return { hero: side.me.hero, position: side.me.position, state: side.me.state };
    const ally = side.allies.find((a) => a.playerId === id);
    if (ally) return { hero: ally.hero, position: ally.position, state: ally.state };
  }
  return null;
}

/** Paneo y atenuación según dónde ocurre respecto del jugador. */
function placement(position, origin) {
  if (!position || !origin) return { pan: 0, gain: 1 };
  const dx = position.x - origin.x;
  const dist = Math.abs(dx) + Math.abs(position.y - origin.y);
  return { pan: clamp(dx / 6, -0.8, 0.8), gain: clamp(1 - dist * 0.05, 0.55, 1) };
}

function soundFor(ev, ctx) {
  const { myId, before, after } = ctx;
  const actor = lookup(ev.actorId, before, after);
  const target = lookup(ev.targetId, before, after);
  const onMe = ev.targetId === myId;
  switch (ev.type) {
    case 'MOVED':
      if (actor?.state === 'EMBER_SPIRIT') return ['emberDrift', { gain: ev.actorId === myId ? 1 : 0.6, wet: 0.35 }, ev.position ?? actor?.position];
      return ctx.moves++ < 3 ? ['step', { gain: ev.actorId === myId ? 1 : 0.6 }, ev.position ?? actor?.position] : null;
    case 'BOUNCED': return ['bounce', {}, ev.position ?? actor?.position];
    // Caminos tras la extinción.
    case 'PATH_CHOSEN': return [ev.detail === 'VENGEANCE' ? 'pathVengeance' : 'pathFriendship', { wet: 0.5, gain: ev.actorId === myId ? 1 : 0.7 }, null];
    case 'SHIELD_KINDLED': return ev.targetId ? ['emberKindle', { wet: 0.4 }, target?.position ?? actor?.position] : null;
    case 'ATTACK_HINDERED': return ['emberHinder', { wet: 0.35 }, target?.position ?? actor?.position];
    case 'BEACON_LIT': return ['emberBeacon', { wet: 0.45, gain: ev.actorId === myId ? 1 : 0.6 }, ev.position ?? actor?.position];
    case 'WARMTH_HEALED': return ['emberWarmth', { wet: 0.4 }, target?.position];
    case 'ECHO_GAINED': return ['echoGained', { opts: { amount: ev.amount ?? 1 }, wet: 0.4 }, null];
    case 'EMBER_REBORN': return ['emberReborn', { wet: 0.5 }, ev.position];
    case 'RENEGADE_SPAWNED': return ['renegadeSpawned', { wet: 0.5 }, ev.position];
    case 'RENEGADE_DEFEATED': return ['renegadeDefeated', { wet: 0.35 }, target?.position];
    case 'ATTACKED':
      if (actor?.enemy) return ['attackEnemy', { opts: { boss: actor.kind === 'CELADOR' } }, actor.position];
      return [HERO_ATTACK[actor?.hero] ?? 'attackYunque', {}, actor?.position ?? ev.position];
    case 'DAMAGED': return ['damage', { opts: { onMe, onEnemy: Boolean(target?.enemy) }, gain: onMe ? 1.1 : 0.9 }, target?.position];
    case 'BLOCKED': return ev.detail === 'WARNING_SHIELD' ? ['warningShield', { wet: 0.3 }, target?.position] : ['block', {}, actor?.position];
    case 'HEALED': return ['heal', { wet: 0.4 }, (target ?? actor)?.position];
    case 'DOWNED': return ['downed', { opts: { onMe } }, target?.position];
    case 'REVIVED': return ['revived', { wet: 0.4 }, target?.position];
    case 'EXTINGUISHED': return ['extinguished', { wet: 0.4 }, target?.position];
    case 'ENEMY_KILLED': return ['enemyKilled', { opts: { boss: target?.kind === 'CELADOR' }, wet: 0.25 }, target?.position];
    case 'ENEMY_SPAWNED': return ['enemySpawned', { wet: 0.3 }, ev.position ?? target?.position];
    case 'CHEST_OPENED': return ['chest', { wet: 0.25 }, ev.position];
    case 'EMBERS_GAINED': return ['embers', { opts: { amount: ev.amount ?? 1 }, wet: 0.35 }, actor?.position];
    case 'EMBERS_STOLEN': return ['embersStolen', { wet: 0.3 }, actor?.position];
    case 'BLESSING_GAINED': return ['blessing', { wet: 0.45 }, actor?.position];
    case 'TRAP_TRIGGERED': return ['trap', {}, ev.position ?? (target ?? actor)?.position];
    case 'FIRE_STARTED': return ['fire', {}, ev.position];
    case 'FROZEN': return ['frozen', { wet: 0.35 }, (target ?? actor)?.position];
    case 'BOSS_PHASE_CHANGED': return ['bossPhase', { wet: 0.4 }, null];
    case 'FLOOR_ENTERED': ctx.floorSound = true; return ['floorEntered', { wet: 0.6 }, null];
    case 'DEFAULT_GUARD_APPLIED': return ev.actorId === myId ? ['defaultGuard', { gain: 0.7 }, null] : null;
    default: return null;
  }
}

function onResolved({ events = [], before, after, myId }) {
  clearTicks();
  playSfx('resolve', { wet: 0.2, gain: 0.8 });
  const origin = after?.me?.position ?? before?.me?.position;
  const ctx = { myId, before: before ?? { allies: [], enemies: [] }, after: after ?? { allies: [], enemies: [] }, moves: 0, floorSound: false };
  // Se escalonan para que se lea la secuencia; un ataque y su daño van casi juntos.
  const step = clamp(1.4 / Math.max(1, events.length), 0.07, 0.16);
  let delay = 0.34;
  let sounded = 0;
  for (const ev of events) {
    const s = soundFor(ev, ctx);
    if (!s) continue;
    const [name, options, position] = s;
    const where = placement(position, origin);
    playSfx(name, { wet: 0.15, ...options, delay, pan: where.pan, gain: (options.gain ?? 1) * where.gain, minGap: 0.02 });
    delay += ev.type === 'ATTACKED' ? 0.07 : ev.type === 'MOVED' ? step * 0.4 : step;
    if (++sounded >= 14) break;
  }
  if (floorPending && !ctx.floorSound) playSfx('floorEntered', { wet: 0.6, delay: 0.3 });
  floorPending = false;
  updateMusic(after, events, myId);
}

function updateMusic(after, events, myId) {
  if (!after) return;
  const me = after.me;
  const enemies = after.enemies ?? [];
  const celador = enemies.find((e) => e.kind === 'CELADOR');
  if (celador) bossSeen = true;
  const pos = me?.position;
  const threatened = Boolean(pos) && enemies.some((e) => (e.telegraph ?? []).some((p) => p.x === pos.x && p.y === pos.y));
  const hurt = events.some((ev) => ev.type === 'DAMAGED' && ev.targetId === myId);
  let target;
  if (bossSeen) {
    const frac = celador?.maxHp ? celador.hp / celador.maxHp : 0.5;
    target = 0.55 + (1 - frac) * 0.35 + (threatened ? 0.1 : 0);
  } else {
    target = Math.min(0.55, enemies.length * 0.2) + (threatened ? 0.35 : 0) + (hurt ? 0.1 : 0);
  }
  const lowHp = me?.state === 'ALIVE' && me.maxHp > 0 && me.hp / me.maxHp <= 0.34;
  setMusicIntensity(target, lowHp);
  if (screen === 'match' || screen === '') setTheme(matchTheme());
  // Caído o extinto: la música se oye como a través de la piedra; como Brasa, lejana y velada.
  if (engine.musicFilter) {
    const cutoff = { DOWN: 700, EXTINCT: 420, EMBER_SPIRIT: 2600 }[me?.state] ?? 18000;
    rampTo(engine.musicFilter.frequency, cutoff, 1.2);
  }
}

function bindBus() {
  on('settings:changed', (s) => { settings = s; applyVolumes(); });
  on('ui:screen', onScreen);
  on('match:start', () => {
    bossSeen = false;
    decided = false;
    playSfx('matchStart', { wet: 0.5, gain: 0.8 });
    setTheme('explore');
  });
  on('match:stop', () => {
    clearTicks();
    if (engine.musicFilter) rampTo(engine.musicFilter.frequency, 18000, 0.5);
  });
  on('round:opened', () => {
    clearTicks();
    decided = false;
    playSfx('roundOpened', { wet: 0.5, gain: 0.8 });
  });
  on('round:low-time', ({ leftMs }) => {
    playSfx('lowTime', { gain: decided ? 0.5 : 1, wet: 0.1 });
    scheduleTicks(leftMs);
  });
  on('player:decided', ({ isMe }) => { if (isMe) decided = true; else playSfx('allyDecided', { wet: 0.2 }); });
  on('order:sent', () => playSfx('orderSent'));
  on('order:acked', () => { decided = true; playSfx('orderAcked', { wet: 0.3 }); });
  on('order:rejected', () => playSfx('orderRejected', { wet: 0.1 }));
  on('round:resolved', onResolved);
  on('floor:changed', () => { bossSeen = false; floorPending = true; });
  on('draft:offer', () => playSfx('draftOffer', { wet: 0.5 }));
  on('draft:picked', () => playSfx('draftPicked', { wet: 0.4 }));
  on('path:offer', () => playSfx('pathOffer', { wet: 0.55, delay: 0.4 }));
  on('path:chosen', ({ path }) => playSfx(path === 'VENGEANCE' ? 'pathVengeance' : 'pathFriendship', { wet: 0.45, gain: 0.8 }));
  on('match:finished', onFinished);
}

export function initAudio() {
  if (started) return;
  started = true;
  engine.muted = Boolean(settings.muted);
  try {
    for (const type of ['pointerdown', 'keydown', 'touchstart']) document.addEventListener(type, unlock, { capture: true, passive: true });
    document.addEventListener('visibilitychange', onVisibility);
    // La página se cierra o se descarta (también al cerrar el panel del navegador): el audio se apaga del todo.
    window.addEventListener('pagehide', shutdown);
    bindInterface();
    bindBus();
  } catch (err) {
    console.warn('audio: desactivado', err);
  }
}
