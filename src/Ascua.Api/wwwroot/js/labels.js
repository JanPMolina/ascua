// Textos en español para los enums del contrato (contracts/asyncapi.yaml).
// Un valor desconocido se muestra tal cual (el contrato permite enums extensibles).

export const HEROES = {
  ERRATA: 'Errata',
  ANTEAYER: 'Anteayer',
  HILVAN: 'Hilván',
  POLIZON: 'Polizón',
  ESCUCHA: 'La Escucha',
  USURERA: 'La Usurera',
  AVARO: 'El Avaro',
  MAMPOSTERO: 'El Mampostero',
  CAMPANERO: 'El Campanero',
};

export const ABILITIES = {
  ERRATA: 'Corregir',
  ANTEAYER: 'Volver',
  HILVAN: 'Tensar',
  POLIZON: 'Abordar',
  ESCUCHA: 'Grito',
  USURERA: 'Préstamo',
  AVARO: 'Soborno',
  MAMPOSTERO: 'Corrimiento',
  CAMPANERO: 'Repique',
};

/** Señales rápidas (SendSignal): frases fijas para el propio bando. */
export const SIGNALS = {
  HERE: { text: '¡Aquí!', icon: '📍' },
  DANGER: { text: '¡Cuidado!', icon: '⚠' },
  HELP: { text: 'Ayuda', icon: '🆘' },
  MINE: { text: 'Es mía', icon: '✋' },
};

export const HERO_STATES = {
  ALIVE: 'vivo', DOWN: 'derribado', EXTINCT: 'muerto', RETIRED: 'retirado', EMBER_SPIRIT: 'Fantasma', RENEGADE: 'Traidor',
};

/** Caminos tras la extinción (planteamiento/04-caminos-tras-morir.md). */
export const PATHS = { FRIENDSHIP: 'Ayudar', VENGEANCE: 'Vengarse' };

/** Órdenes de la Brasa errante: mismos tipos del contrato, otro efecto. */
export const EMBER_ORDERS = {
  MOVE: { name: 'Flotar', icon: '〰', text: 'Avanza hasta 2 casillas en esa dirección, atravesando muros, enemigos y héroes.' },
  ATTACK: { name: 'Frenar', icon: '⧖', text: 'El primer enemigo a 2 casillas o menos retrasa una ronda su ataque marcado. Espera 2.' },
  ABILITY: { name: 'Escudo', icon: '❂', text: 'El primer aliado a 2 casillas o menos recibe +1 de escudo (máx. 2 de este escudo). +1 de apoyo por cada punto que absorba.' },
  INTERACT: { name: 'Revivir', icon: '✚', text: 'Levanta con 1 de vida a un aliado derribado en la casilla vecina. +3 de apoyo.' },
  GUARD: { name: 'Luz', icon: '☼', text: 'Ilumina un radio de 2 a tu alrededor para todo el grupo. +1 de apoyo por cada golpe final dentro.' },
};

/** Por qué ganó ecos la Brasa (ECHO_GAINED.detail). */
export const ECHO_REASONS = {
  SHIELD_ABSORBED: 'tu escudo absorbió un golpe',
  REKINDLED: 'reviviste a un aliado',
  ATTACK_HINDERED: 'frenaste un golpe a un héroe',
  BEACON_KILL: 'un aliado dio un golpe final bajo tu Luz',
  WARMTH: 'tu calidez curó a un aliado',
};

export const MATCH_STATUS = {
  LOBBY: 'sala', DRAFTING: 'eligiendo mejora y misión', IN_PROGRESS: 'en curso', FINISHED: 'terminada',
  FAULTED: 'fallida', ABANDONED: 'abandonada', EXPIRED: 'expirada', TERMINATED: 'terminada por un admin',
};

export const ORDER_TYPES = { MOVE: 'Paso', ATTACK: 'Atacar', ABILITY: 'Habilidad', INTERACT: 'Usar', GUARD: 'Defender', PUSH: 'Empujar' };
export const DIRECTIONS = { N: 'norte', S: 'sur', E: 'este', W: 'oeste' };

export const BLESSINGS = {
  BRASA_VIVA: 'Brasa viva', PASO_IGNEO: 'Paso ígneo', PUNO_DE_PIEDRA: 'Puño de piedra',
  CORAZON_DOBLE: 'Corazón doble', HOJA_HAMBRIENTA: 'Hoja hambrienta', ESPEJO_ROTO: 'Espejo roto',
  LAMPARA: 'Lámpara', GANZUA: 'Ganzúa', ULTIMO_ALIENTO: 'Último aliento', CODICIA: 'Codicia',
  ESCARCHA: 'Escarcha', OJO_DE_BUHO: 'Ojo de búho',
};

/** Qué hace cada mejora (planteamiento/06-version-3.md §8). `catalog.js` lo reemplaza con lo que manda el servidor. */
export const BLESSING_EFFECTS = {
  BRASA_VIVA: 'Tus golpes siempre queman: 1 de daño más al terminar la ronda.',
  PASO_IGNEO: 'Cada paso deja fuego en la casilla que dejas.',
  PUNO_DE_PIEDRA: 'Si atacas sin dar un paso, haces +1 de daño.',
  CORAZON_DOBLE: '+2 de vida máxima (el Avaro, +1 de oro).',
  HOJA_HAMBRIENTA: 'Cada golpe final te cura 1 (el Avaro, +1 de oro).',
  ESPEJO_ROTO: 'Todo golpe con autor que recibes le devuelve 1 de daño.',
  LAMPARA: 'Ves toda la sala.',
  GANZUA: 'Tus cofres dan +2 de oro.',
  ULTIMO_ALIENTO: 'Una vez: al caer quedas con 1 de vida.',
  CODICIA: '+1 de oro por cada golpe final.',
  ESCARCHA: 'Tus golpes congelan: pierde su próxima acción.',
  OJO_DE_BUHO: 'Ves todos los ataques marcados de la sala.',
};

/** Lo que cuesta cada mejora: siempre a la vista. */
export const BLESSING_COSTS = {
  BRASA_VIVA: 'Recibes 1 de daño al abrir cofres.',
  PASO_IGNEO: 'El fuego te hace 1 más a ti.',
  PUNO_DE_PIEDRA: '−1 de vida máxima (el Avaro, −1 de oro).',
  CORAZON_DOBLE: 'Nada te cura.',
  HOJA_HAMBRIENTA: 'No puedes defender.',
  ESPEJO_ROTO: 'No puedes tener escudo.',
  LAMPARA: 'Los monstruos te eligen siempre.',
  GANZUA: 'No puedes levantar a aliados con Usar.',
  ULTIMO_ALIENTO: 'Si vuelves a caer, nadie puede levantarte.',
  CODICIA: 'Cada golpe que recibes hace +1.',
  ESCARCHA: 'Tus golpes no hacen daño (al Celador y a un Traidor, que no se congelan, sí).',
  OJO_DE_BUHO: 'Visión −1, mínimo 1.',
};

/** Tipos de sala (también vienen con la oferta y en la vista). */
export const ROOM_TYPES = {
  NORMAL: { name: 'Normal', effect: 'Sin regla propia.', icon: '▫' },
  TREASURE: { name: 'Tesoro', effect: '4 cofres y 1 monstruo más.', icon: '📦' },
  ARENA: { name: 'Arena', effect: 'Sin cofres, 1 o 2 monstruos más y cada golpe final da 2 de oro.', icon: '⚔' },
  SANCTUARY: { name: 'Santuario', effect: 'Pocos monstruos, un altar que no pide llave y más trampas.', icon: '⛩' },
  DARK: { name: 'Oscura', effect: 'Todos ven 1 casilla menos y hay 1 cofre más.', icon: '🌑' },
  FLOODED: { name: 'Inundada', effect: 'Desde el norte, cada 6 rondas se inunda una fila más: 1 de daño por ronda a quien no vuela.', icon: '🌊' },
};

/** Maldiciones: las elige el anfitrión y son públicas. */
export const CURSES = {
  FIERCE: { name: 'Fieros', effect: 'Los monstruos tienen 1 de vida más.', icon: '🐾' },
  WARDEN_PLUS: { name: 'Celador imponente', effect: 'El Celador tiene 50 % más de vida.', icon: '👑' },
  DIM: { name: 'Penumbra', effect: 'Todos ven 1 casilla menos.', icon: '🌘' },
  HASTE: { name: 'Prisa', effect: 'Las rondas duran 7 segundos.', icon: '⏱' },
};

/** Fases de la resolución (`phase` de los eventos), en orden. */
export const PHASES = {
  REVEAL: 'Caminos', GUARD: 'Defensas', SHIFT: 'La sala se corre', MOVEMENT: 'Pasos', ACTIONS: 'Acciones',
  ENEMY_ATTACKS: 'Golpes enemigos', ENEMY_MOVES: 'Los enemigos avanzan', CLEANUP: 'Final de la ronda', END: 'Fin',
};

export const ENEMIES = {
  CARRONERO: 'Carroñero', POLILLA: 'Polilla', GELATINA: 'Gelatina', SALTASAPO: 'Saltasapo',
  BALLESTERO: 'Ballestero', TOTEM: 'Tótem', CELADOR: 'Celador',
};

export const OUTCOMES = {
  WARDEN_DEFEATED: 'El Celador cayó',
  DRAW: 'El Celador cayó — empate en oro',
  EMBER_EXTINGUISHED: 'El grupo perdió: el Celador sigue en pie',
  ABANDONED: 'Partida abandonada',
  FAULTED: 'La partida falló en el servidor',
  TERMINATED: 'Un administrador terminó la partida',
  THRESHOLD_CLAIMED: 'Ganan los Traidores',
};

export const EVENTS = {
  MOVED: 'se movió', BOUNCED: 'rebotó', ATTACKED: 'atacó', DAMAGED: 'recibió daño', BLOCKED: 'bloqueó',
  HEALED: 'se curó', DOWNED: 'fue derribado', REVIVED: 'fue levantado', EXTINGUISHED: 'murió',
  ENEMY_KILLED: 'mató a un enemigo', ENEMY_SPAWNED: 'apareció un enemigo', CHEST_OPENED: 'abrió un cofre',
  EMBERS_GAINED: 'ganó oro', EMBERS_STOLEN: 'robó oro', BLESSING_GAINED: 'obtuvo una mejora',
  TRAP_TRIGGERED: 'activó una trampa', FIRE_STARTED: 'empezó un fuego', FROZEN: 'quedó congelado',
  BOSS_PHASE_CHANGED: 'el Celador cambió de fase', FLOOR_ENTERED: 'entraron a un piso nuevo',
  DEFAULT_GUARD_APPLIED: 'defendió por defecto (no envió orden)', ABILITY_USED: 'usó su habilidad', GUARDED: 'defendió',
  PATH_OFFERED: 'debe elegir camino', PATH_SELECTED: 'eligió camino', PATH_CHOSEN: 'tomó un camino',
  EMBER_REBORN: 'renació en la escalera', RENEGADE_SPAWNED: 'apareció como Traidor', RENEGADE_DEFEATED: 'derrotó a un Traidor',
  ECHO_GAINED: 'ganó puntos de apoyo', SHIELD_KINDLED: 'dio escudo a un aliado', ATTACK_HINDERED: 'frenó a un enemigo',
  BEACON_LIT: 'encendió una Luz', WARMTH_HEALED: 'calentó a un aliado',
  ECHO_FIRED: 'disparó un Eco', ATTACK_CORRECTED: 'un golpe marcado se desvió', BETRAYAL_RECORDED: 'traicionó en secreto',
  BETRAYAL_REVEALED: 'traición revelada', HOST_BOARDED: 'abordó a un monstruo', HOST_EXITED: 'reapareció', HOST_DRAINED: 'drenó a su huésped',
  WARDEN_TURNED: 'el Celador giró', SHOUTED: 'gritó', DEBT_CHANGED: 'la deuda cambió', EMBERS_DROPPED: 'cayeron oro al suelo',
  EMBERS_PICKED_UP: 'recogió oro', EMBERS_LOST: 'perdió oro', ENEMY_BRIBED: 'sobornó a un enemigo', ROOM_SHIFTED: 'la sala se corrió',
  BELL_RUNG: 'tocó el Repique', BELL_EXPIRED: 'venció el plazo del Repique',
  PUSHED: 'empujó', ROOM_FLOODED: 'el agua subió', CHILLED: 'llegó el frío',
};

export const ERROR_CODES = {
  INVALID_PAYLOAD: 'Mensaje inválido',
  INVALID_ORDER: 'Orden ilegal en este momento',
  STALE_ROUND: 'La ronda ya cerró',
  ORDER_ALREADY_SUBMITTED: 'Ya enviaste otra orden en esta ronda',
  ORDER_TOO_LATE: 'Se te acabó el plazo del Repique: defiendes esta ronda',
  NOT_PARTICIPANT: 'No participas en esta partida',
  NOT_JOINED: 'La conexión no está unida a la partida',
  PLAYER_DOWN: 'Tu héroe no puede actuar',
  MATCH_NOT_FOUND: 'La partida no existe',
  MATCH_NOT_ACTIVE: 'La partida no está activa',
  ALREADY_IN_ANOTHER_MATCH: 'Ya estás en otra partida',
  DRAFT_CLOSED: 'La elección de mejora ya cerró',
  INVALID_PICK: 'Esa mejora no estaba en la oferta',
  PATH_CLOSED: 'La elección de camino ya cerró',
  PACT_CLOSED: 'La elección de misión ya cerró',
  VOTE_CLOSED: 'La votación ya cerró o ya votaste',
  INVALID_VOTE: 'Ese voto no vale',
  RATE_LIMITED: 'Demasiados mensajes; espera un momento',
  MESSAGE_TOO_LARGE: 'Mensaje demasiado grande',
  INTERNAL_ERROR: 'Error del servidor',
};

export const label = (table, value) => (value in table ? table[value] : String(value));
