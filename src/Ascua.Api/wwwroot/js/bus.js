// Bus de eventos del cliente: el controlador de la partida (match.js) anuncia lo que pasa y los módulos de
// presentación (sonido, animaciones, interfaz) se suscriben sin tocar la lógica de la partida.
//
// Eventos (detail):
//   match:start      { mode: 'online' | 'demo' | 'tutorial' }
//   match:stop       {}
//   conn:state       { text, kind }                    (kind: '' | 'ok' | 'warn' | 'bad')
//   draft:offer      { options, source }
//   draft:picked     { blessingId }
//   path:offer       { savedEmbers, wardenRoom }       (al extinguirse: se abre la elección de camino)
//   path:chosen      { path }                          (ChoosePath aceptado; path: 'FRIENDSHIP' | 'VENGEANCE')
//   round:opened     { round, durationMs }
//   round:low-time   { round, leftMs }                 (una vez por ronda, al quedar < 5 s)
//   player:decided   { playerId, isMe, decidedCount, expectedPlayers }
//   order:sent       { order }                         ({ type, direction? })
//   order:acked      { order }
//   order:rejected   { order, code }                   (code del servidor, o CLIENT_BLOCKED si el cliente no la envió)
//   round:resolved   { round, events, before, after, myId }
//                    before/after: { me, allies, enemies } con posiciones, vida y estado (copias)
//   floor:changed    { floor }
//   match:finished   { outcome, iWon, standings, myId }
//   ui:screen        { name }                          (ui.showScreen)
//   settings:changed { muted, sfxVolume, musicVolume, reducedMotion }

const listeners = new Map();

export function on(type, fn) {
  if (!listeners.has(type)) listeners.set(type, new Set());
  listeners.get(type).add(fn);
  return () => listeners.get(type)?.delete(fn);
}

export function emit(type, detail = {}) {
  for (const fn of listeners.get(type) ?? []) {
    // Un efecto que falla (p. ej. el audio bloqueado por el navegador) nunca rompe la partida.
    try { fn(detail); } catch (err) { console.warn(`bus: ${type}`, err); }
  }
}
