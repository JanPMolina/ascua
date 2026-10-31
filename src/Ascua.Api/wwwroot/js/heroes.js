// Fichas de los héroes para la interfaz (planteamiento/01-juego.md §4): retrato, rol, vida y habilidad.
// Los nombres de los enums siguen labels.js; un héroe desconocido cae en una ficha genérica.
import { el } from './ui.js';

export const HERO_INFO = {
  YUNQUE: {
    name: 'Yunque', role: 'Tanque', hp: 6, color: '#8fa3b8',
    passive: '25% de bloquear golpes de frente.',
    ability: 'Muralla', abilityText: 'Protege a los aliados que estén detrás de él esa ronda.',
  },
  CHISPA: {
    name: 'Chispa', role: 'Distancia', hp: 4, color: '#f5a65b',
    passive: 'Dispara a 3 casillas en línea; gana 1 carga por ronda sin atacar.',
    ability: 'Descarga', abilityText: 'Golpe en cruz con 1 + cargas de daño.',
  },
  CUERVO: {
    name: 'Cuervo', role: 'Pícaro', hp: 4, color: '#b49cf0',
    passive: 'Doble daño por la espalda.',
    ability: 'Paso sombrío', abilityText: 'Avanza 2 casillas y queda invisible 2 rondas.',
  },
  RAIZ: {
    name: 'Raíz', role: 'Soporte', hp: 5, color: '#7cc47f',
    passive: 'Las curaciones le rinden +1.',
    ability: 'Brote', abilityText: 'Cura 2 a un aliado cercano o a sí misma.',
  },
};

export const heroInfo = (id) => HERO_INFO[id] ?? {
  name: String(id ?? '—'), role: '', hp: 0, color: '#a79d92', passive: '', ability: '', abilityText: '',
};

/** Ícono de cada bendición (chips del HUD y cartas del draft). */
export const BLESSING_ICONS = {
  BRASA_VIVA: '🔥', PASO_IGNEO: '👣', PUNO_DE_PIEDRA: '✊', CORAZON_DOBLE: '💞', HOJA_HAMBRIENTA: '🗡',
  ESPEJO_ROTO: '🪞', LAMPARA: '🏮', GANZUA: '🗝', ULTIMO_ALIENTO: '🕯', CODICIA: '💰', ESCARCHA: '❄', OJO_DE_BUHO: '🦉',
};

const PORTRAITS ={ YUNQUE: 'yunque', CHISPA: 'chispa', CUERVO: 'cuervo', RAIZ: 'raiz' };

/** Retrato del héroe (img/heroes/*.svg). Decorativo: el nombre siempre va escrito al lado. */
export function heroAvatar(id, size = 'md') {
  const file = PORTRAITS[id];
  if (!file) return el('span', { class: `avatar avatar-${size} avatar-empty`, 'aria-hidden': 'true' });
  return el('img', { class: `avatar avatar-${size}`, src: `img/heroes/${file}.svg`, alt: '', width: 64, height: 64, decoding: 'async' });
}
