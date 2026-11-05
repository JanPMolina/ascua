// Textos de mejoras, salas y maldiciones que manda el servidor (GET /api/v1/catalog). labels.js trae una copia para la
// demo y el tutorial, y por si la petición falla; aquí se reemplaza con la que manda.
import * as api from './api.js';
import { BLESSINGS, BLESSING_COSTS, BLESSING_EFFECTS, CURSES, ROOM_TYPES } from './labels.js';

let loaded = false;

/** Carga el catálogo una vez. Si falla, se sigue con los textos locales. */
export async function loadCatalog() {
  if (loaded) return false;
  try {
    const catalog = await api.catalog();
    for (const b of catalog.blessings ?? []) {
      BLESSINGS[b.id] = b.name;
      BLESSING_EFFECTS[b.id] = b.effect;
      BLESSING_COSTS[b.id] = b.cost;
    }

    for (const r of catalog.roomTypes ?? []) ROOM_TYPES[r.id] = { ...ROOM_TYPES[r.id], name: r.name, effect: r.effect };
    for (const c of catalog.curses ?? []) CURSES[c.id] = { ...CURSES[c.id], name: c.name, effect: c.effect };
    loaded = true;
    return true;
  } catch {
    return false;
  }
}
