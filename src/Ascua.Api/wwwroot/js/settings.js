// Preferencias del jugador (volumen, movimiento reducido). Se guardan en localStorage solo como
// comodidad: si el navegador no deja guardar, se usan los valores por defecto sin fallar.
import { emit } from './bus.js';

const KEY = 'ascua.settings.v1';
const prefersReducedMotion = () => globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;

const defaults = () => ({ muted: false, sfxVolume: 0.8, musicVolume: 0.5, reducedMotion: prefersReducedMotion() });

let current = load();

function load() {
  try {
    const raw = localStorage.getItem(KEY);
    return raw ? { ...defaults(), ...JSON.parse(raw) } : defaults();
  } catch {
    return defaults();
  }
}

export function getSettings() { return { ...current }; }

export function updateSettings(patch) {
  current = { ...current, ...patch };
  try { localStorage.setItem(KEY, JSON.stringify(current)); } catch { /* sin almacenamiento: solo en memoria */ }
  emit('settings:changed', getSettings());
}
