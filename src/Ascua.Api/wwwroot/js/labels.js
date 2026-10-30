// Textos en español para los enums del contrato (contracts/asyncapi.yaml).
// Un valor desconocido se muestra tal cual (el contrato permite enums extensibles).

export const HEROES = {
  YUNQUE: 'Yunque (tanque)',
  CHISPA: 'Chispa (distancia)',
  CUERVO: 'Cuervo (pícaro)',
  RAIZ: 'Raíz (soporte)',
};

export const ABILITIES = {
  YUNQUE: 'Muralla',
  CHISPA: 'Descarga',
  CUERVO: 'Paso sombrío',
  RAIZ: 'Brote',
};

export const HERO_STATES = { ALIVE: 'vivo', DOWN: 'caído', EXTINCT: 'extinto', RETIRED: 'retirado' };

export const MATCH_STATUS = {
  LOBBY: 'sala', DRAFTING: 'draft', IN_PROGRESS: 'en curso', FINISHED: 'terminada',
  FAULTED: 'fallida', ABANDONED: 'abandonada', EXPIRED: 'expirada', TERMINATED: 'terminada por un admin',
};

export const ORDER_TYPES = { MOVE: 'Mover', ATTACK: 'Atacar', ABILITY: 'Habilidad', INTERACT: 'Interactuar', GUARD: 'Guardar' };
export const DIRECTIONS = { N: 'norte', S: 'sur', E: 'este', W: 'oeste' };

export const BLESSINGS = {
  BRASA_VIVA: 'Brasa viva', PASO_IGNEO: 'Paso ígneo', PUNO_DE_PIEDRA: 'Puño de piedra',
  CORAZON_DOBLE: 'Corazón doble', HOJA_HAMBRIENTA: 'Hoja hambrienta', ESPEJO_ROTO: 'Espejo roto',
  LAMPARA: 'Lámpara', GANZUA: 'Ganzúa', ULTIMO_ALIENTO: 'Último aliento', CODICIA: 'Codicia',
  ESCARCHA: 'Escarcha', OJO_DE_BUHO: 'Ojo de búho',
};

export const ENEMIES = {
  CARRONERO: 'Carroñero', POLILLA: 'Polilla', GELATINA: 'Gelatina', SALTASAPO: 'Saltasapo',
  BALLESTERO: 'Ballestero', TOTEM: 'Tótem', CELADOR: 'Celador',
};

export const OUTCOMES = {
  WARDEN_DEFEATED: 'El Celador cayó',
  DRAW: 'El Celador cayó — empate en ascuas',
  EMBER_EXTINGUISHED: 'La Ascua se apagó (derrota colectiva)',
  ABANDONED: 'Partida abandonada',
  FAULTED: 'La partida falló en el servidor',
  TERMINATED: 'Un administrador terminó la partida',
};

export const EVENTS = {
  MOVED: 'se movió', BOUNCED: 'rebotó', ATTACKED: 'atacó', DAMAGED: 'recibió daño', BLOCKED: 'bloqueó',
  HEALED: 'se curó', DOWNED: 'cayó', REVIVED: 'fue levantado', EXTINGUISHED: 'se extinguió',
  ENEMY_KILLED: 'mató a un enemigo', ENEMY_SPAWNED: 'apareció un enemigo', CHEST_OPENED: 'abrió un cofre',
  EMBERS_GAINED: 'ganó ascuas', EMBERS_STOLEN: 'robó ascuas', BLESSING_GAINED: 'obtuvo una bendición',
  TRAP_TRIGGERED: 'activó una trampa', FIRE_STARTED: 'empezó un fuego', FROZEN: 'quedó congelado',
  BOSS_PHASE_CHANGED: 'el Celador cambió de fase', FLOOR_ENTERED: 'entraron a un piso nuevo',
  DEFAULT_GUARD_APPLIED: 'guardó por defecto (no envió orden)',
};

export const ERROR_CODES = {
  INVALID_PAYLOAD: 'Mensaje inválido',
  INVALID_ORDER: 'Orden ilegal en este momento',
  STALE_ROUND: 'La ronda ya cerró',
  ORDER_ALREADY_SUBMITTED: 'Ya enviaste otra orden en esta ronda',
  NOT_PARTICIPANT: 'No participas en esta partida',
  NOT_JOINED: 'La conexión no está unida a la partida',
  PLAYER_DOWN: 'Tu héroe no puede actuar',
  MATCH_NOT_FOUND: 'La partida no existe',
  MATCH_NOT_ACTIVE: 'La partida no está activa',
  ALREADY_IN_ANOTHER_MATCH: 'Ya estás en otra partida',
  DRAFT_CLOSED: 'El draft ya cerró',
  INVALID_PICK: 'Esa bendición no estaba en la oferta',
  RATE_LIMITED: 'Demasiados mensajes; espera un momento',
  MESSAGE_TOO_LARGE: 'Mensaje demasiado grande',
  INTERNAL_ERROR: 'Error del servidor',
};

export const label = (table, value) => (value in table ? table[value] : String(value));
