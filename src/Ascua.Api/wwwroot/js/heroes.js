// Fichas de los héroes para la interfaz (planteamiento/05-heroes.md §2): retrato, regla que rompe, pasiva y activa.
// Los nombres de los enums siguen labels.js; un héroe desconocido cae en una ficha genérica.
import { el } from './ui.js';

export const HERO_INFO = {
  ERRATA: {
    name: 'Errata', role: 'Correctora', hp: 4, range: 1, initiative: 6, cooldown: 3, color: '#e0566e',
    breaks: 'Los golpes caen donde se marcaron',
    passiveName: 'Fe de erratas', passive: 'Ve todos los ataques marcados de la sala, aunque estén en la niebla (solo las casillas, no quién los marca).',
    ability: 'Corregir', abilityText: 'Todo enemigo con alguna casilla marcada a 2 o menos desplaza su ataque marcado entero 1 casilla en esa dirección. Si le cae a un aliado, nadie sabrá que fuiste tú… hasta el final.',
  },
  ANTEAYER: {
    name: 'Anteayer', role: 'Reincidente', hp: 4, range: 1, initiative: 5, cooldown: 5, color: '#6fd8c8',
    breaks: 'Lo hecho, hecho está',
    passiveName: 'Eco', passive: 'Dos rondas después de cada ataque, una copia translúcida lo repite desde donde estabas y en esa dirección.',
    ability: 'Volver', abilityText: 'Saltas a donde estabas hace 2 rondas y, si tenías más vida entonces, la recuperas.',
  },
  HILVAN: {
    name: 'Hilván', role: 'Dos cuerpos', hp: 8, range: 1, initiative: 3, cooldown: 3, color: '#e8a0d0',
    breaks: 'Tienes un solo cuerpo',
    passiveName: 'Espejo', passive: 'Dos cuerpos, una orden y una vida. Norte y sur los mueven igual; este y oeste, al revés. Ve con los dos.',
    ability: 'Tensar', abilityText: 'Con los cuerpos alineados, a 2 o más y sin muro entre ellos, todo lo que haya entre ellos recibe 2 de daño, aliados incluidos.',
  },
  POLIZON: {
    name: 'Polizón', role: 'Parásito', hp: 6, range: 1, initiative: 7, cooldown: 3, color: '#a8d85a',
    breaks: 'Eres tu cuerpo',
    passiveName: 'Sin casa', passive: 'Dentro de un monstruo desapareces de la vista de todos, aliados incluidos. Cada 2 rondas lo drenas: pierde 1 y tú curas 1.',
    ability: 'Abordar', abilityText: 'Entras en el enemigo vecino. Dentro, mover lo mueve, atacar hace que golpee ya, y la habilidad te saca. Al Celador solo lo giras 180°.',
  },
  ESCUCHA: {
    name: 'La Escucha', role: 'Ciega', hp: 5, range: 4, initiative: 2, cooldown: 4, color: '#b0a4f0',
    breaks: 'Ves lo que tienes cerca',
    passiveName: 'Ciega', passive: 'Conoce el terreno, pero solo percibe lo que está a 1 casilla y lo que hizo ruido. Lo quieto no existe. Golpea a tientas hasta 4 casillas: 2 de daño a lo que no hizo ruido.',
    ability: 'Grito', abilityText: 'Hasta la próxima resolución todo su bando ve la sala entera, y en esta ronda todos los monstruos la eligen a ella.',
  },
  USURERA: {
    name: 'La Usurera', role: 'Prestamista', hp: 4, range: 1, initiative: 1, cooldown: 2, color: '#e8c44a',
    breaks: 'Ayudar es gratis',
    passiveName: 'Interés', passive: 'Cada 7 rondas la deuda sube 1 (tope 3). El oro que gane el deudor le llegan a ella; al terminar cobra lo que quede.',
    ability: 'Préstamo', abilityText: 'Al primer aliado herido o derribado en línea a 2 o menos: cura 3 o lo levanta con 3. No pide permiso. Le crea 1 de oro de deuda.',
  },
  AVARO: {
    name: 'El Avaro', role: 'Sin vida', hp: 4, hpIsEmbers: true, range: 1, initiative: 2, cooldown: 2, color: '#ff9a3a',
    breaks: 'La vida y la puntuación son cosas distintas',
    passiveName: 'Todo lo que tengo', passive: 'Su oro es su vida: empieza con 4. Cada punto de daño tira 1 al suelo, y el primero que pise la casilla se lo queda. No acepta curas ni préstamos.',
    ability: 'Soborno', abilityText: 'Paga 1 de oro al primer enemigo en línea a 3 o menos: cancela su ataque marcado y esa ronda no actúa. Al Celador le cuesta 3 y solo cancela el ataque marcado.',
  },
  MAMPOSTERO: {
    name: 'El Mampostero', role: 'Albañil', hp: 6, range: 1, initiative: 1, cooldown: 3, color: '#b8aa98',
    breaks: 'La mazmorra está quieta',
    passiveName: 'Conoce las piedras', passive: 'Los pinchos y las grietas no le afectan.',
    ability: 'Corrimiento', abilityText: 'Desliza 1 casilla su fila o su columna con todo lo que tenga encima, escalera incluida. Lo que sale por un borde entra por el otro y, si es una criatura, recibe 2 de daño.',
  },
  CAMPANERO: {
    name: 'El Campanero', role: 'Último en hablar', hp: 4, range: 2, initiative: 4, cooldown: 4, color: '#7fb0e8',
    breaks: 'El reloj es igual para todos',
    passiveName: 'Último en hablar', passive: 'Si todos los demás ya decidieron y él no, recibe en privado sus órdenes y puede responder con lo que quede de reloj.',
    ability: 'Repique', abilityText: 'En la ronda siguiente todos los demás tienen solo 8 s; él conserva los 20. Se oye: todos ven la cuenta regresiva.',
  },
};

export const heroInfo = (id) => HERO_INFO[id] ?? {
  name: String(id ?? '—'), role: '', hp: 0, range: 1, initiative: 0, cooldown: 0, color: '#a79d92', breaks: '', passiveName: '', passive: '', ability: '', abilityText: '',
};

/** Ícono de cada bendición (chips del HUD y cartas del draft). */
export const BLESSING_ICONS = {
  BRASA_VIVA: '🔥', PASO_IGNEO: '👣', PUNO_DE_PIEDRA: '✊', CORAZON_DOBLE: '💞', HOJA_HAMBRIENTA: '🗡',
  ESPEJO_ROTO: '🪞', LAMPARA: '🏮', GANZUA: '🗝', ULTIMO_ALIENTO: '🕯', CODICIA: '💰', ESCARCHA: '❄', OJO_DE_BUHO: '🦉',
};

/** Retrato del héroe (img/heroes/*.svg). Decorativo: el nombre siempre va escrito al lado. */
export function heroAvatar(id, size = 'md') {
  if (!HERO_INFO[id]) return el('span', { class: `avatar avatar-${size} avatar-empty`, 'aria-hidden': 'true' });
  return el('img', { class: `avatar avatar-${size}`, src: `img/heroes/${id.toLowerCase()}.svg`, alt: '', width: 64, height: 64, decoding: 'async' });
}
