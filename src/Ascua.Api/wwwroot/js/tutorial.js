// Tutorial interactivo (?tutorial=1): una partida de práctica en el navegador, sin cuenta ni servidor,
// sobre el mismo "servidor" simulado de la demo (demo.js). Un guía propone un objetivo por paso y
// avanza cuando el jugador lo cumple; cada paso prepara lo que necesita (p. ej. hace aparecer un enemigo).
import { $, el, setText } from './ui.js';
import { DEMO_PLAYER_ID, DemoTransport } from './demo.js';

const ME_ID = DEMO_PLAYER_ID;

const SCENARIO = {
  // Sin aliados: la ronda se resuelve apenas envías tu orden, sin esperar a nadie.
  roundMs: 60000,
  layout: [
    '.........',
    '.........',
    '..#...#..',
    '....S....',
    '.........',
    '.........',
    '..C......',
    '.........',
    '.........',
  ],
  draftOptions: ['LAMPARA', 'BRASA_VIVA'],
  pactOptions: [
    { pactId: 'OPEN_1_CHEST', description: 'Abrir 1 cofre.' },
    { pactId: 'OPEN_3_CHESTS', description: 'Abrir 3 cofres.' },
  ],
  curses: [],
  me: {
    playerId: ME_ID, displayName: 'aprendiz', hero: 'MAMPOSTERO', state: 'ALIVE', hp: 6, maxHp: 6, shield: 0,
    position: { x: 4, y: 7 }, facing: 'N', embers: 0, abilityCooldown: 0,
    downRoundsLeft: null, blessings: [], pact: { pactId: 'OPEN_1_CHEST', description: 'Abrir 1 cofre.' },
    decided: false, pendingOrder: null,
  },
  allies: [],
  allyPacts: {},
  enemies: [],
  autoFinish: false,
  forgiving: true,
  holdDraft: true,
};

const manhattan = (a, b) => Math.abs(a.x - b.x) + Math.abs(a.y - b.y);

/** Dirección (N/S/E/O) desde a hacia b si están en la misma fila o columna; si no, null. */
function lineDir(a, b) {
  if (a.x === b.x) return b.y < a.y ? 'N' : 'S';
  if (a.y === b.y) return b.x < a.x ? 'W' : 'E';
  return null;
}

class TutorialTransport extends DemoTransport {
  constructor() {
    super(SCENARIO);
    this.protocolName = 'tutorial';
    this.listener = () => {};
  }

  push(type, payload) {
    super.push(type, payload);
    if (this.isConnected) this.listener(type, payload);
  }

  async invoke(method, payload) {
    const result = await super.invoke(method, payload);
    if (result?.error) this.listener('InvokeError', { method, error: result.error });
    else if (method === 'ChoosePath') this.listener('PathChosen', result);
    return result;
  }
}

/**
 * Pasos del tutorial. Cada uno puede tener:
 * - setup(c): prepara el tablero al empezar el paso;
 * - text(c): párrafos del guía (se recalculan tras cada ronda, para dar pistas según la posición);
 * - focus: ids o selectores que se resaltan;
 * - done(c, type, msg): true cuando el objetivo se cumplió;
 * - button: si existe, el paso avanza con ese botón en lugar de con una acción en el tablero;
 * - actions(c): botones secundarios opcionales [{ label, onClick }] bajo el texto.
 */
const STEPS = [
  {
    title: 'Bienvenida a Ascua',
    text: () => [
      'Ascua es una mazmorra por turnos simultáneos para 2 a 5 jugadores. Juntos deben derrotar al Celador antes de que se apague el fuego… pero solo gana quien reúna más oro.',
      'En esta práctica juegas con El Mampostero, y sin otros jugadores. Hay nueve héroes y cada uno rompe una regla del juego: el suyo rompe «la mazmorra está quieta». Nada de lo que hagas aquí cuenta, así que puedes equivocarte sin problema.',
      'Tu héroe aparece en el panel resaltado. Ahí verás también tu misión secreta: un objetivo que solo tú conoces y que da +3 de oro si lo cumples.',
    ],
    focus: ['#me-pact'],
    button: 'Empezar',
  },
  {
    title: 'Elige mejora y misión',
    setup: (c) => c.server.sendOffer(),
    text: () => [
      'Al empezar cada jugador elige 1 mejora entre 2 y 1 misión secreta entre 2. Nadie más sabe cuáles tomaste.',
      'Cada mejora tiene un precio: léelo antes de elegir. Elige las dos cosas. Si se acaba el plazo, se eligen solas.',
    ],
    done: (c) => c.server.me.blessings.length > 0 && Boolean(c.server.me.pact),
  },
  {
    title: 'Fase 1: moverte',
    text: (c) => [
      'Cada ronda planeas en dos fases, con clics: 1 Moverte y 2 Actuar. Luego pulsas Listo. Mira el indicador «1 Moverte → 2 Actuar → Listo» en el panel de la derecha.',
      'Fase 1: las casillas vecinas a las que puedes caminar están resaltadas. Haz clic en una (o en tu héroe para quedarte). Después pulsa Listo: si no eliges acción, solo caminas.',
      c.server.round > 0 ? 'Cuando todos están listos (o vence el plazo, 11 s en partidas reales) se resuelve todo a la vez. Ves solo hasta 3 casillas; lo demás queda en niebla.' : 'Espera a que se abra la ronda 1 (mira el reloj arriba del tablero).',
    ],
    focus: ['#phase-steps', '#board', '#btn-ready'],
    done: (c, type) => type === 'RoundResolved' && c.server.lastOrder?.type === 'MOVE',
  },
  {
    title: 'Ataques marcados y Defender',
    setup: (c) => c.spawnNear('CARRONERO', 2, 2),
    text: (c) => {
      const danger = c.dangerOnMe();
      return [
        'Apareció un Carroñero. Los enemigos MARCAN dónde van a golpear: esas casillas se ven en rojo y el golpe cae en la ronda siguiente.',
        danger
          ? '¡La casilla roja está sobre ti! En la fase 2 pulsa el botón Defender (recibes la mitad del daño y anulas los golpes de 1) y luego Listo. También podrías apartarte en la fase 1.'
          : 'Todavía no te apunta. Pulsa Defender y Listo para esperar a que se acerque, y mira cómo marca su ataque.',
        'Si una ronda no planeas nada, defiendes solo.',
      ];
    },
    focus: ['.act[data-act="GUARD"]', '#btn-ready', '#board'],
    done: (c, type) => type === 'RoundResolved' && c.dangerBefore && !c.hitThisRound(),
  },
  {
    title: 'Fase 2: atacar',
    setup: (c) => { if (c.enemies().length === 0) c.spawnNear('CARRONERO', 2, 2); },
    text: (c) => {
      const target = c.nearestEnemy();
      const lines = ['Fase 2: pulsa el botón Atacar. Se resaltan las casillas a las que llega tu golpe desde donde quedarás; haz clic en la del enemigo y luego en Listo. El Mampostero pega a la casilla vecina; otros héroes llegan más lejos.'];
      if (!target) return [...lines, 'No ves enemigos ahora: pulsa Listo y espera una ronda.'];
      lines.push(manhattan(c.me().position, target.position) <= 1 && lineDir(c.me().position, target.position)
        ? `Lo tienes al lado. Tiene ${target.hp} de vida: puede que necesites más de un golpe.`
        : 'Todavía no está a tu lado: en la fase 1 haz clic en una casilla que te acerque.');
      lines.push('Dar el golpe final a un enemigo da +1 de oro.');
      return lines;
    },
    focus: ['.act[data-act="ATTACK"]', '#board'],
    done: (c, type, msg) => type === 'RoundResolved' && c.mine(msg, 'ENEMY_KILLED'),
  },
  {
    title: 'Las dos fases juntas',
    setup: (c) => { if (c.enemies().length === 0) c.spawnNear('CARRONERO', 2, 2, 3); },
    text: (c) => {
      const target = c.nearestEnemy();
      const lines = ['En la misma ronda puedes moverte y actuar: primero caminan todos, luego actúa cada uno desde donde quedó. Los objetivos se resaltan desde la casilla de llegada.'];
      if (!target) return [...lines, 'No ves enemigos ahora: pulsa Listo y espera una ronda.'];
      lines.push('Haz clic en la casilla que te deja al lado del enemigo, luego en Atacar, en el enemigo, y en Listo. El panel resume tu plan: «Te mueves al norte y atacas al Carroñero».');
      lines.push('Puedes cambiar el plan hasta pulsar Listo (Borrar plan empieza de nuevo). Si se acaba el tiempo, se envía lo que planeaste. Defender borra el paso: quien defiende se planta.');
      return lines;
    },
    focus: ['#plan-summary', '#phase-steps', '#board'],
    done: (c, type) => type === 'RoundResolved' && Boolean(c.server.lastOrder?.step),
  },
  {
    title: 'Abre el cofre',
    text: (c) => {
      const chest = c.chest();
      if (!chest) return ['Ya abriste el cofre.'];
      const me = c.me().position;
      const dist = manhattan(me, chest);
      return [
        'Los cofres (casillas doradas) dan oro y a veces objetos. Su contenido solo lo ve quien lo abre.',
        dist === 1
          ? 'Estás al lado: en la fase 2 pulsa Usar, haz clic en el cofre resaltado y luego en Listo.'
          : dist === 2 && lineDir(me, chest)
          ? 'Puedes hacerlo en una sola ronda: en la fase 1 haz clic en la casilla junto al cofre; en la fase 2, Usar, clic en el cofre y Listo.'
          : `El cofre está en (${chest.x},${chest.y}). Acércate: en la fase 1 haz clic en la casilla vecina que te acerque.`,
        c.me().pact?.pactId === 'OPEN_1_CHEST' ? 'Abrirlo también cumple tu misión secreta de esta práctica.' : 'Cada cofre cuenta para tu misión secreta.',
      ];
    },
    focus: ['.act[data-act="INTERACT"]', '#tile-embers'],
    done: (c, type, msg) => type === 'RoundResolved' && c.mine(msg, 'CHEST_OPENED'),
  },
  {
    title: 'Usa tu habilidad',
    setup: (c) => c.spawnAtEdge('POLILLA'),
    text: (c) => {
      const cd = c.me().abilityCooldown;
      return [
        'Cada héroe tiene una habilidad. La tuya es Corrimiento: desliza 1 casilla tu fila (este/oeste) o tu columna (norte/sur) con todo lo que tenga encima, también la escalera. Lo que sale por un borde entra por el otro y, si es una criatura, recibe 2 de daño.',
        cd > 0
          ? `La habilidad está en espera: lista en ${cd} ronda${cd === 1 ? '' : 's'} (lo dice su ficha, bajo los botones). Defiende hasta entonces.`
          : 'Apareció una Polilla al borde de tu fila. En la fase 2 pulsa el botón Corrimiento: se resaltan tu fila y tu columna. Haz clic en una casilla de la fila hacia la Polilla y luego en Listo. ¡En partidas reales también arrastra a tus aliados!',
      ];
    },
    focus: ['.act[data-act="ABILITY"]', '#ability-card'],
    done: (c, type) => type === 'RoundResolved' && c.server.lastOrder?.type === 'ABILITY' && c.mine(c.lastResolved, 'ABILITY_USED'),
  },
  {
    title: 'Despeja la sala',
    setup: (c) => { if (c.enemies().length === 0) c.spawnNear('GELATINA', 2, 3); },
    text: (c) => {
      const left = c.server.enemies.size;
      return [
        left > 0
          ? `Quedan ${left} enemigo${left === 1 ? '' : 's'}. Usa lo que aprendiste: defiende cuando te apunten, ataca a lo que tengas al lado y corre la sala cuando un enemigo esté en tu línea.`
          : '¡Sala despejada!',
        'En una partida real, al despejar una sala se baja por la escalera (casilla azul). Tras 4 salas espera el Celador, que hay que flanquear: es inmune a los golpes de frente.',
      ];
    },
    focus: ['#board'],
    done: (c) => c.server.enemies.size === 0,
  },
  {
    title: 'Empujar y la escalera',
    text: () => [
      'Empujar (botón Empujar y clic en la casilla de al lado): mueve 1 casilla a quien tengas al lado, aliado o enemigo. Se ve quién fue y luego esperas 3 rondas. Un enemigo que no vuela muere en una grieta; un héroe queda derribado.',
      'Al despejar una sala, pisar la escalera lleva a todos directo a la siguiente. Su tipo sale al azar y se anuncia al entrar: tesoro, arena, santuario, oscura o inundada.',
      'No hay límite de rondas, pero desde la ronda 80 llega el frío: daño cada 3 rondas que nada para. No se demoren.',
    ],
    focus: ['.act[data-act="PUSH"]', '#board'],
    button: 'Sigue',
  },
  {
    // Opcional: se explica y se puede ver el diálogo, pero no hace falta jugarlos.
    title: 'Si mueres: dos caminos',
    setup: (c) => { c.server.paused = true; },
    text: (c) => {
      const lines = [
        'En una partida real, un héroe derribado que nadie levanta en 3 rondas muere. Su oro queda guardado y tiene 15 s para elegir:',
        'Ayudar: vagas como Fantasma, un espíritu azul que atraviesa muros y ayuda al grupo. Cada ayuda da puntos de apoyo, y al bajar de piso renaces con más oro cuantos más puntos de apoyo tengas.',
        'Vengarse: vuelves como Traidor, del lado de la mazmorra. Robas oro a los héroes y, si todos caen, ganas tú.',
      ];
      if (c.practiceResult) lines.push(c.practiceResult);
      return lines;
    },
    actions: (c) => (c.practiceUsed ? [] : [{ label: 'Ver la elección (opcional)', onClick: () => c.practicePath() }]),
    button: 'Terminar el tutorial',
  },
];

class Coach {
  constructor(transport) {
    this.t = transport;
    this.server = transport.server;
    this.index = -1;
    this.dangerBefore = false;
    this.lastResolved = null;
    this.focused = [];
    transport.listener = (type, payload) => this.onMessage(type, payload);
  }

  // ───── estado del tablero (lo que ve el jugador) ─────
  me() { return this.server.me; }

  enemies() { return [...this.server.enemies.values()].filter((e) => this.server.visible(e.position)); }

  nearestEnemy() {
    const me = this.me().position;
    return this.enemies().sort((a, b) => manhattan(a.position, me) - manhattan(b.position, me))[0] ?? null;
  }

  chest() {
    for (const [k, kind] of this.server.tiles) {
      if (kind === 'CHEST') { const [x, y] = k.split(',').map(Number); return { x, y }; }
    }
    return null;
  }

  dangerOnMe() {
    const { x, y } = this.me().position;
    return this.enemies().some((e) => e.telegraph.some((p) => p.x === x && p.y === y));
  }

  mine(msg, type) { return (msg?.delta?.events ?? []).some((ev) => ev.type === type && ev.actorId === ME_ID); }

  hitThisRound() {
    return (this.lastResolved?.delta?.events ?? []).some((ev) => ev.type === 'DAMAGED' && ev.targetId === ME_ID);
  }

  /** Hace aparecer un enemigo a una distancia entre min y max (visible), en una casilla libre. */
  spawnNear(kind, min, max, hp = 2) {
    const me = this.me().position;
    const spots = [];
    for (let y = 0; y < 9; y++) {
      for (let x = 0; x < 9; x++) {
        const d = manhattan(me, { x, y });
        if (d >= min && d <= max && !this.server.blocked(x, y) && this.server.tiles.get(`${x},${y}`) === 'FLOOR') spots.push({ x, y, d });
      }
    }
    // Preferir casillas al norte (hacia donde avanza el jugador) y en línea, para que la lección sea clara.
    spots.sort((a, b) => (b.y < me.y) - (a.y < me.y) || (lineDir(me, b) !== null) - (lineDir(me, a) !== null) || a.d - b.d);
    const spot = spots[0];
    if (!spot) return;
    this.server.spawn({ enemyId: this.nextId(), kind, hp, position: { x: spot.x, y: spot.y } });
    this.refresh();
  }

  /** Enemigo de 1 de vida en la fila del jugador, pegado a un borde, para que el Corrimiento lo saque por ahí. */
  spawnAtEdge(kind) {
    const { y } = this.me().position;
    const spot = [8, 0].map((x) => ({ x, y })).find((p) => !this.server.blocked(p.x, p.y));
    if (spot) this.server.spawn({ enemyId: this.nextId(), kind, hp: 1, position: spot });
    this.refresh();
  }

  nextId() {
    this.idSeq = (this.idSeq ?? 100) + 1;
    return this.idSeq;
  }

  /** Reenvía la vista completa para que el tablero muestre lo que el paso acaba de preparar. */
  refresh() { this.t.push('Snapshot', this.server.snapshot()); }

  /** Muestra el diálogo de caminos sin cambiar la partida; si vence el plazo, toma la Amistad como el servidor. */
  practicePath() {
    this.practiceUsed = true;
    this.render();
    const ms = 30000;
    const offer = this.server.sendPracticePathOffer(6, ms);
    this.server.later(ms, () => {
      if (offer.choice) return;
      const me = this.me();
      offer.choice = 'FRIENDSHIP';
      me.path = { offerId: offer.offerId, choice: 'FRIENDSHIP', savedEmbers: 6, echoes: 0, rebirthEmbers: null, hinderCooldown: 0, awaitingSpawn: null, spawnRound: null };
      this.refresh();
      me.path = null;
      this.onMessage('PathChosen', { path: 'FRIENDSHIP' });
    });
  }

  // ───── avance ─────
  start() {
    $('coach').hidden = false;
    $('coach-exit').onclick = () => { location.href = '/'; };
    this.goTo(0);
  }

  goTo(index) {
    this.index = index;
    const step = STEPS[index];
    if (!step) {
      this.finish();
      return;
    }
    step.setup?.(this);
    this.dangerBefore = this.dangerOnMe();
    this.render();
    // Paso nuevo: el guía vuelve a mostrarse desde su título (su columna o él mismo pueden tener scroll).
    const coach = $('coach');
    coach.scrollTop = 0;
    if (coach.parentElement) coach.parentElement.scrollTop = 0;
  }

  onMessage(type, payload) {
    if (type === 'MatchFinished') {
      this.showEnd(payload);
      return;
    }
    const step = STEPS[this.index];
    if (!step) return;
    if (type === 'RoundResolved') this.lastResolved = payload;
    if (type === 'PathChosen') {
      this.practiceResult = payload.path === 'VENGEANCE'
        ? 'Elegiste Vengarse. Aquí no pasa nada más: en una partida volverías como Traidor en la sala siguiente.'
        : 'Elegiste Ayudar. Aquí no pasa nada más: en una partida seguirías como Fantasma hasta bajar de piso.';
      this.render();
      return;
    }
    if (type === 'InvokeError' && payload.error.code === 'INVALID_ORDER') {
      this.note = 'Esa orden no es válida ahora. Lee la pista de arriba.';
    }
    if (step.done?.(this, type, payload)) {
      this.note = null;
      this.goTo(this.index + 1);
      return;
    }
    if (type === 'RoundResolved') {
      if (step.title === 'Ataques marcados' && this.dangerBefore && this.hitThisRound()) {
        this.note = 'Te golpeó: estabas en la casilla roja sin defender. No pasa nada, inténtalo otra vez.';
      }
      this.dangerBefore = this.dangerOnMe();
    }
    if (type === 'RoundResolved' || type === 'RoundOpened' || type === 'Snapshot' || type === 'InvokeError') this.render();
  }

  render() {
    const step = STEPS[this.index];
    if (!step) return;
    setText('coach-step', `Paso ${this.index + 1} de ${STEPS.length}`);
    setText('coach-title', step.title);
    const paragraphs = step.text(this).map((t) => el('p', {}, t));
    if (this.note) paragraphs.push(el('p', { class: 'coach-note' }, this.note));
    for (const action of step.actions?.(this) ?? []) {
      paragraphs.push(el('button', { type: 'button', class: 'secondary small', onclick: action.onClick }, action.label));
    }
    $('coach-body').replaceChildren(...paragraphs);
    $('coach-bar').style.width = `${(this.index / STEPS.length) * 100}%`;
    const next = $('coach-next');
    next.hidden = !step.button;
    if (step.button) {
      next.textContent = step.button;
      next.onclick = () => this.goTo(this.index + 1);
      next.focus();
    }
    this.setFocus(step.focus ?? []);
  }

  setFocus(selectors) {
    this.focused.forEach((node) => node.classList.remove('tut-focus'));
    this.focused = selectors.flatMap((s) => [...document.querySelectorAll(s)]);
    this.focused.forEach((node) => node.classList.add('tut-focus'));
  }

  finish() {
    setText('coach-step', 'Completado');
    setText('coach-title', '¡Lo lograste!');
    $('coach-body').replaceChildren(el('p', {}, 'Ya sabes planear las dos fases con clics, defender, atacar, abrir cofres y usar tu habilidad.'));
    $('coach-bar').style.width = '100%';
    $('coach-next').hidden = true;
    this.setFocus([]);
    // El servidor simulado anuncia el fin como en una partida real.
    setTimeout(() => this.server.finish('WARDEN_DEFEATED'), 1200);
  }

  showEnd() {
    this.setFocus([]);
    $('coach').hidden = true;
    $('fin-tutorial').hidden = false;
    $('btn-back-lobby').hidden = true;
    setText('fin-title', 'Tutorial completado');
  }
}

/** Arranca el tutorial. `startMatch` es el de match.js (se inyecta para no crear un ciclo de imports). */
export function startTutorial(startMatch) {
  const transport = new TutorialTransport();
  const coach = new Coach(transport);
  startMatch({ transport, onExit: () => { location.href = '/'; } });
  coach.start();
}
