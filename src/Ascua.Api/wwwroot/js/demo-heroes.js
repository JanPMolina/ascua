// Los nueve héroes en el "servidor" de la demo (demo.js). No es el motor de reglas (src/Ascua.Domain/Rules): imita lo
// suficiente para ver en pantalla cada mecánica de planteamiento/05-heroes.md, con los mismos mensajes y campos del contrato.
//
// Los bots hacen lo mínimo para mostrar lo que los demás ven de cada héroe: Hilván lleva dos cuerpos, Polizón desaparece a ratos,
// el Campanero toca el Repique cada 5 rondas, la Usurera te presta sin pedir permiso.
import { HERO_INFO } from './heroes.js';

const DIRS = { N: [0, -1], S: [0, 1], E: [1, 0], W: [-1, 0] };
const key = (x, y) => `${x},${y}`;
const clone = (v) => JSON.parse(JSON.stringify(v));
const inBounds = (x, y) => x >= 0 && y >= 0 && x < 9 && y < 9;
const same = (a, b) => Boolean(a && b) && a.x === b.x && a.y === b.y;
const manhattan = (a, b) => Math.abs(a.x - b.x) + Math.abs(a.y - b.y);
const mirrored = (d) => ({ E: 'W', W: 'E' }[d] ?? d);
const STOPS = new Set(['WALL', 'BRAZIER']);

const COOLDOWN_WARDEN_TURN = 6;
const DRAIN_EVERY = 2;
const ALLY_BELL_EVERY = 5;
const BELL_MS = 8000;
export { BELL_MS };

/** Héroes de los aliados: los pedidos o los que más se notan en pantalla, sin repetir el propio. */
export function pickAllies(hero, wanted) {
  const pool = [...(wanted ?? []), 'HILVAN', 'POLIZON', 'CAMPANERO', 'MAMPOSTERO', 'USURERA'];
  return [...new Set(pool)].filter((h) => h !== hero && HERO_INFO[h]).slice(0, 3);
}

/** Vida inicial: el Avaro no tiene barra; sus ascuas son su vida. */
export function heroStats(hero) {
  const info = HERO_INFO[hero];
  return info.hpIsEmbers ? { hp: 1, maxHp: 1, embers: info.hp } : { hp: info.hp, maxHp: info.hp, embers: 0 };
}

export function newHeroState() {
  return { echoes: [], ground: [], noise: [], history: [], shout: false, bell: null, bellNext: null, betrayals: new Map(), lastWordSent: false, bellExpired: null };
}

const canStand = (s, x, y) => inBounds(x, y) && !s.blocked(x, y);

/** Segundo cuerpo de Hilván a 2 casillas en la misma fila (o la otra mano si no cabe). */
export function placeSecondBody(s, hero) {
  const { x, y } = hero.position;
  const spot = [2, -2, 1, -1].map((d) => ({ x: x + d, y })).find((p) => canStand(s, p.x, p.y));
  hero.secondBody = spot ?? null;
}

/** Primer objetivo en línea hasta `range`; devuelve el enemigo o el aliado, o null. Muros y braseros detienen el golpe. */
function firstInLine(s, from, dir, range, { allies = true } = {}) {
  const [dx, dy] = DIRS[dir];
  for (let i = 1; i <= range; i++) {
    const p = { x: from.x + dx * i, y: from.y + dy * i };
    if (!inBounds(p.x, p.y) || STOPS.has(s.tiles.get(key(p.x, p.y)))) return null;
    const enemy = [...s.enemies.values()].find((e) => same(e.position, p));
    if (enemy) return { enemy };
    if (allies) {
      const ally = s.allies.find((a) => a.position && (a.state === 'ALIVE' || a.state === 'DOWN') && same(a.position, p));
      if (ally) return { ally };
    }
  }
  return null;
}

function noteBetrayal(s, traitorId, victim, damage, events, actorId) {
  const k = `${traitorId}>${victim.playerId}`;
  s.heroes.betrayals.set(k, (s.heroes.betrayals.get(k) ?? 0) + damage);
  if (traitorId === actorId) events.push({ type: 'BETRAYAL_RECORDED', actorId: traitorId, targetId: victim.playerId, amount: damage });
}

/** Ataque básico del héroe propio, con su alcance. Anteayer deja un Eco; Hilván golpea con los dos cuerpos. */
export function heroAttack(s, order, events, by) {
  const me = s.me;
  const range = HERO_INFO[me.hero].range ?? 1;
  const strike = (from, dir) => {
    const found = firstInLine(s, from, dir, range);
    if (found?.enemy) {
      // La Escucha hace 2 a lo que no hizo ruido.
      const quiet = me.hero === 'ESCUCHA' && !s.heroes.noise.some((p) => same(p, found.enemy.position));
      s.hit(found.enemy, quiet ? 2 : 1, events, by);
    } else if (found?.ally && me.state !== 'RENEGADE') {
      const victim = found.ally;
      if (victim.state === 'ALIVE') s.damageHero(victim, 1, by, events);
    }
  };
  strike(me.position, order.direction);
  if (me.secondBody) strike(me.secondBody, mirrored(order.direction));
  events.push({ type: 'ATTACKED', actorId: by, position: clone(me.position) });
  if (me.hero === 'ANTEAYER' && me.state === 'ALIVE') {
    s.heroes.echoes.push({ ownerId: by, position: clone(me.position), direction: order.direction, fireRound: s.round + 2 });
  }
}

/** Mover con los cuerpos que haya; devuelve true si el principal cambió de casilla. */
export function heroMove(s, order, events, by) {
  const me = s.me;
  const body = (pos, dir, detail) => {
    const [dx, dy] = DIRS[dir];
    const x = pos.x + dx;
    const y = pos.y + dy;
    if (s.blocked(x, y)) {
      events.push({ type: 'BOUNCED', actorId: by, position: clone(pos), ...(detail ? { detail } : {}) });
      return pos;
    }
    events.push({ type: 'MOVED', actorId: by, position: { x, y }, ...(detail ? { detail } : {}) });
    return { x, y };
  };
  me.position = body(me.position, order.direction);
  if (me.secondBody) me.secondBody = body(me.secondBody, mirrored(order.direction), 'SECOND_BODY');
}

/** Dentro del huésped: mover lo mueve, atacar golpea ya (y si le da a un aliado, nadie sabe quién fue). */
export function hostOrder(s, order, events, by) {
  const me = s.me;
  const host = s.enemies.get(me.hostEnemyId);
  if (!host) { me.hostEnemyId = null; return; }
  if (order.type === 'MOVE') {
    const [dx, dy] = DIRS[order.direction];
    const x = host.position.x + dx;
    const y = host.position.y + dy;
    host.facing = order.direction;
    if (!s.blocked(x, y) || (me.position.x === x && me.position.y === y)) {
      host.position = { x, y };
      me.position = { x, y };
    }
  } else if (order.type === 'ATTACK') {
    host.facing = order.direction;
    const found = firstInLine(s, host.position, order.direction, host.kind === 'BALLESTERO' ? 8 : 1);
    events.push({ type: 'ATTACKED', actorId: `enemy:${host.enemyId}`, position: clone(host.position) });
    if (found?.enemy && found.enemy !== host) s.hit(found.enemy, 1, events, by);
    else if (found?.ally?.state === 'ALIVE') {
      s.damageHero(found.ally, 1, `enemy:${host.enemyId}`, events);
      noteBetrayal(s, by, found.ally, 1, events, by);
    }
  } else if (order.type === 'ABILITY') {
    leaveHost(s, order.direction, events, by);
    me.abilityCooldown = HERO_INFO.POLIZON.cooldown + 1;
    s.hindered.add(host.enemyId);
  }
}

function leaveHost(s, dir, events, by) {
  const me = s.me;
  const host = s.enemies.get(me.hostEnemyId);
  const base = host?.position ?? me.position;
  const order = dir ? [dir, ...Object.keys(DIRS).filter((d) => d !== dir)] : Object.keys(DIRS);
  const spot = order.map((d) => ({ x: base.x + DIRS[d][0], y: base.y + DIRS[d][1] })).find((p) => canStand(s, p.x, p.y));
  me.hostEnemyId = null;
  me.position = spot ?? clone(base);
  events.push({ type: 'HOST_EXITED', actorId: by, position: clone(me.position) });
}

/** Si otro mata al huésped, Polizón sale en esa casilla y recibe 1. */
export function hostKilled(s, enemy, events) {
  const me = s.me;
  if (me.hostEnemyId !== enemy.enemyId) return;
  me.hostEnemyId = null;
  me.position = clone(enemy.position);
  events.push({ type: 'HOST_EXITED', actorId: me.playerId, position: clone(me.position) });
  s.damageHero(me, 1, `enemy:${enemy.enemyId}`, events);
}

/** Corrimiento: rota la línea con todo lo que tenga encima; quien cruza el borde recibe 2. */
function roomShift(s, dir, events, tilesChanged, by) {
  const me = s.me;
  const horizontal = dir === 'E' || dir === 'W';
  const index = horizontal ? me.position.y : me.position.x;
  const sign = dir === 'E' || dir === 'S' ? 1 : -1;
  const onLine = (p) => (horizontal ? p.y === index : p.x === index);
  const wrap = (p) => {
    if (!p || !onLine(p)) return p;
    const v = ((horizontal ? p.x : p.y) + sign + 9) % 9;
    return horizontal ? { x: v, y: p.y } : { x: p.x, y: v };
  };
  const crossed = (p) => (horizontal ? p.x : p.y) === (sign > 0 ? 8 : 0);

  const line = [];
  for (let i = 0; i < 9; i++) {
    const p = horizontal ? { x: i, y: index } : { x: index, y: i };
    line.push({ to: wrap(p), kind: s.tiles.get(key(p.x, p.y)) });
  }
  for (const c of line) s.tiles.set(key(c.to.x, c.to.y), c.kind);
  for (const c of line) if (s.seen.has(key(c.to.x, c.to.y))) tilesChanged.push({ x: c.to.x, y: c.to.y, kind: c.kind });
  events.push({ type: 'ROOM_SHIFTED', actorId: by, amount: index, detail: `${horizontal ? 'ROW' : 'COLUMN'}_${dir}` });

  const hurt = [];
  for (const e of s.enemies.values()) {
    if (!onLine(e.position)) continue;
    if (crossed(e.position)) hurt.push(() => s.hit(e, 2, events, by));
    e.position = wrap(e.position);
  }
  for (const a of s.allies) {
    if (!a.position || a.state === 'EMBER_SPIRIT') continue;
    if (onLine(a.position)) {
      if (crossed(a.position) && a.state === 'ALIVE') hurt.push(() => s.damageHero(a, 2, by, events));
      a.position = wrap(a.position);
    }
    if (a.secondBody) a.secondBody = wrap(a.secondBody);
  }
  if (onLine(me.position)) {
    if (crossed(me.position)) hurt.push(() => s.damageHero(me, 2, by, events));
    me.position = wrap(me.position);
  }
  for (const g of s.heroes.ground) g.position = wrap(g.position);
  hurt.forEach((fn) => fn());
  s.enemies.forEach((e) => s.updateTelegraph(e));
}

/** Corregir: desplaza los anuncios con alguna casilla a ≤2; lo que cae en un muro o fuera se pierde. */
function correct(s, dir, events, by) {
  const [dx, dy] = DIRS[dir];
  let any = false;
  for (const e of s.enemies.values()) {
    if (!e.telegraph.some((p) => manhattan(p, s.me.position) <= 2)) continue;
    any = true;
    e.telegraph = e.telegraph.map((p) => ({ x: p.x + dx, y: p.y + dy })).filter((p) => inBounds(p.x, p.y) && s.tiles.get(key(p.x, p.y)) !== 'WALL');
    e.correctedBy = by;
    const shown = e.telegraph.filter((p) => s.visible(p));
    if (shown.length) events.push({ type: 'ATTACK_CORRECTED', ...(s.visible(e.position) ? { targetId: `enemy:${e.enemyId}` } : {}), tiles: shown });
  }
  return any;
}

/** Habilidad del héroe propio. Devuelve true si hizo algo: una habilidad que no hace nada no gasta el enfriamiento. */
export function heroAbility(s, order, events, tilesChanged, by) {
  const me = s.me;
  const info = HERO_INFO[me.hero];
  const dir = order.direction;
  const renegade = me.state === 'RENEGADE';
  const spend = (cd = info.cooldown) => { me.abilityCooldown = (renegade ? cd * 2 : cd) + 1; };
  const used = () => events.push({ type: 'ABILITY_USED', actorId: by, position: clone(me.position) });
  switch (me.hero) {
    case 'ERRATA':
      if (correct(s, dir, events, by)) { spend(); events.push({ type: 'ABILITY_USED', actorId: by, position: clone(me.position) }); }
      break;
    case 'ANTEAYER': {
      if (!me.rewindTile) break;
      me.position = canStand(s, me.rewindTile.x, me.rewindTile.y) || same(me.position, me.rewindTile) ? clone(me.rewindTile) : me.position;
      if ((me.rewindHp ?? 0) > me.hp) me.hp = me.rewindHp;
      spend();
      used();
      break;
    }
    case 'HILVAN': {
      const a = me.position;
      const b = me.secondBody;
      if (!b || (a.x !== b.x && a.y !== b.y) || manhattan(a, b) < 2) break;
      const dx = Math.sign(b.x - a.x);
      const dy = Math.sign(b.y - a.y);
      const cells = [];
      let blocked = false;
      for (let p = { x: a.x + dx, y: a.y + dy }; !same(p, b); p = { x: p.x + dx, y: p.y + dy }) {
        if (s.tiles.get(key(p.x, p.y)) === 'WALL') blocked = true;
        cells.push(p);
      }
      if (blocked) break;
      spend();
      used();
      for (const p of cells) {
        const enemy = [...s.enemies.values()].find((e) => same(e.position, p));
        if (enemy) s.hit(enemy, 2, events, by);
        const ally = s.allies.find((x) => x.state === 'ALIVE' && same(x.position, p));
        if (ally) s.damageHero(ally, 2, by, events);
      }
      break;
    }
    case 'POLIZON': {
      const [dx, dy] = DIRS[dir];
      const p = { x: me.position.x + dx, y: me.position.y + dy };
      const enemy = [...s.enemies.values()].find((e) => same(e.position, p));
      if (!enemy || enemy.kind === 'TOTEM') break;
      used();
      if (enemy.kind === 'CELADOR') {
        const turned = { N: 'S', S: 'N', E: 'W', W: 'E' }[enemy.facing] ?? 'N';
        enemy.facing = turned;
        spend(COOLDOWN_WARDEN_TURN);
        events.push({ type: 'WARDEN_TURNED', targetId: `enemy:${enemy.enemyId}`, detail: turned });
        break;
      }
      me.hostEnemyId = enemy.enemyId;
      me.position = clone(enemy.position);
      enemy.telegraph = [];
      enemy.boardedRound = s.round;
      events.push({ type: 'HOST_BOARDED', actorId: by, targetId: `enemy:${enemy.enemyId}` });
      break;
    }
    case 'ESCUCHA':
      spend();
      used();
      s.heroes.shout = true;
      events.push({ type: 'SHOUTED', actorId: by });
      break;
    case 'USURERA': {
      const found = firstInLine(s, me.position, dir, 2);
      const ally = found?.ally;
      if (!ally || ally.hero === 'AVARO' || (ally.state === 'ALIVE' && ally.hp >= ally.maxHp)) break;
      spend();
      used();
      if (ally.state === 'DOWN') {
        ally.state = 'ALIVE';
        ally.hp = 3;
        ally.downRoundsLeft = null;
        events.push({ type: 'REVIVED', actorId: by, targetId: ally.playerId });
      } else {
        const healed = Math.min(3, ally.maxHp - ally.hp);
        ally.hp += healed;
        events.push({ type: 'HEALED', targetId: ally.playerId, amount: healed });
      }
      ally.debtToYou = (ally.debtToYou ?? 0) + 1;
      events.push({ type: 'DEBT_CHANGED', actorId: by, targetId: ally.playerId, amount: 1, detail: 'LOAN' });
      break;
    }
    case 'AVARO': {
      const enemy = firstInLine(s, me.position, dir, 3, { allies: false })?.enemy;
      if (!enemy) break;
      const price = enemy.kind === 'CELADOR' ? 3 : 1;
      if (me.embers <= price) break;
      me.embers -= price;
      spend();
      used();
      enemy.telegraph = [];
      if (enemy.kind !== 'CELADOR') s.hindered.add(enemy.enemyId);
      events.push({ type: 'ENEMY_BRIBED', actorId: by, targetId: `enemy:${enemy.enemyId}`, amount: price });
      events.push({ type: 'EMBERS_LOST', actorId: by, amount: -price, detail: 'BRIBE' });
      break;
    }
    case 'MAMPOSTERO':
      spend();
      used();
      roomShift(s, dir, events, tilesChanged, by);
      break;
    case 'CAMPANERO':
      spend();
      used();
      s.heroes.bellNext = by;
      events.push({ type: 'BELL_RUNG', actorId: by, amount: s.round + 1 });
      break;
    default:
      break;
  }
}

/** Eco programado: repite el golpe desde donde estaba; el daño es de su dueño. */
function fireEchoes(s, events) {
  const due = s.heroes.echoes.filter((e) => e.fireRound === s.round);
  s.heroes.echoes = s.heroes.echoes.filter((e) => e.fireRound !== s.round);
  for (const e of due) {
    events.push({ type: 'ECHO_FIRED', actorId: e.ownerId, position: clone(e.position), detail: e.direction });
    const found = firstInLine(s, e.position, e.direction, HERO_INFO.ANTEAYER.range);
    if (found?.enemy) s.hit(found.enemy, 1, events, e.ownerId);
    else if (found?.ally?.state === 'ALIVE') s.damageHero(found.ally, 1, e.ownerId, events);
  }
}

/** Ascuas en el suelo: las recoge el primero que pisa la casilla; quien debe paga antes. */
function pickUpGround(s, events) {
  const walkers = [s.me, ...s.allies].filter((h) => h.position && (h.state === 'ALIVE' || h.state === 'RENEGADE'));
  s.heroes.ground = s.heroes.ground.filter((pile) => {
    const who = walkers.find((h) => (same(h.position, pile.position) && h.hostEnemyId == null) || same(h.secondBody, pile.position));
    if (!who) return true;
    who.embers += pile.amount;
    events.push({ type: 'EMBERS_PICKED_UP', actorId: who.playerId, position: clone(pile.position), amount: pile.amount });
    return false;
  });
}

/** Lo que se hace antes de los enemigos: ecos y bots con habilidad. */
export function beforeEnemies(s, events) {
  pickUpGround(s, events);
  fireEchoes(s, events);
}

function allyWith(s, hero) { return s.allies.find((a) => a.hero === hero && a.state === 'ALIVE'); }

/** Fin de la resolución: drenaje, deudas, historia de Volver, ruido y lo que hacen los bots con habilidad. */
export function afterRound(s, events, positionsBefore) {
  const me = s.me;
  const alive = me.state === 'ALIVE' || me.state === 'RENEGADE';
  // Enfriamiento propio (la Brasa lleva el suyo).
  if (me.state !== 'EMBER_SPIRIT') me.abilityCooldown = Math.max(0, (me.abilityCooldown ?? 0) - 1);

  // Drenaje del huésped.
  if (me.hostEnemyId != null) {
    const host = s.enemies.get(me.hostEnemyId);
    if (host) {
      const inside = s.round - (host.boardedRound ?? s.round);
      if (inside > 0 && inside % DRAIN_EVERY === 0) {
        events.push({ type: 'HOST_DRAINED', actorId: me.playerId, targetId: `enemy:${host.enemyId}` });
        me.hp = Math.min(me.maxHp, me.hp + 1);
        s.hit(host, 1, events, me.playerId);
      }
    }
  }

  // Deudas: lo que tengas paga. La Usurera ve lo suyo; el deudor, lo suyo.
  if (me.debt > 0 && me.embers > 0) {
    me.embers -= 1;
    me.debt -= 1;
    events.push({ type: 'DEBT_CHANGED', actorId: me.debtTo, targetId: me.playerId, amount: -1, detail: 'PAID' });
    events.push({ type: 'EMBERS_LOST', actorId: me.playerId, amount: -1, detail: 'DEBT_SETTLED' });
    if (!me.debt) me.debtTo = null;
  }
  if (me.hero === 'USURERA') {
    for (const a of s.allies) {
      if (a.debtToYou > 0 && a.embers > 0) {
        a.embers -= 1;
        a.debtToYou -= 1;
        me.embers += 1;
        if (!a.debtToYou) a.debtToYou = null;
        events.push({ type: 'DEBT_CHANGED', actorId: me.playerId, targetId: a.playerId, amount: -1, detail: 'PAID' });
        events.push({ type: 'EMBERS_GAINED', actorId: me.playerId, amount: 1, detail: 'DEBT_COLLECTED' });
      }
    }
  }

  // La Usurera aliada te presta sin pedir permiso.
  const lender = allyWith(s, 'USURERA');
  if (lender && me.state === 'ALIVE' && me.hero !== 'AVARO' && me.hp < me.maxHp && !me.debt && s.round % 3 === 0) {
    const healed = Math.min(3, me.maxHp - me.hp);
    me.hp += healed;
    me.debt = 1;
    me.debtTo = lender.playerId;
    events.push({ type: 'HEALED', targetId: me.playerId, amount: healed });
    events.push({ type: 'DEBT_CHANGED', actorId: lender.playerId, targetId: me.playerId, amount: 1, detail: 'LOAN' });
  }

  // Hilván aliado: su segundo cuerpo lo sigue en espejo.
  for (const a of s.allies) if (a.hero === 'HILVAN' && a.position && a.state !== 'EMBER_SPIRIT' && a.state !== 'EXTINCT') placeSecondBody(s, a);

  // Polizón aliado: a ratos no hay rastro de él. Al salir se ve reaparecer.
  const stowaway = allyWith(s, 'POLIZON');
  if (stowaway) {
    const hidden = s.round % 6 >= 3;
    if (stowaway.hiddenInHost && !hidden && s.visible(stowaway.position)) events.push({ type: 'HOST_EXITED', actorId: stowaway.playerId, position: clone(stowaway.position) });
    stowaway.hiddenInHost = hidden;
  }

  // Campanero aliado: cada 5 rondas, Repique.
  const ringer = allyWith(s, 'CAMPANERO');
  if (ringer && s.round % ALLY_BELL_EVERY === 0 && !s.heroes.bellNext) {
    s.heroes.bellNext = ringer.playerId;
    events.push({ type: 'BELL_RUNG', actorId: ringer.playerId, amount: s.round + 1 });
  }
  if (s.heroes.bellExpired) {
    events.push({ type: 'BELL_EXPIRED', actorId: s.heroes.bellExpired });
    s.heroes.bellExpired = null;
  }

  // Volver: adónde saltaría (hace 2 rondas) y con cuánta vida.
  if (alive && me.hero === 'ANTEAYER') {
    s.heroes.history.push({ position: clone(me.position), hp: me.hp });
    const past = s.heroes.history[s.heroes.history.length - 3];
    me.rewindTile = past ? clone(past.position) : null;
    me.rewindHp = past ? past.hp : null;
  }

  // Ruido de la ronda: se movió o golpeó. Lo quieto no existe para La Escucha; las Brasas no hacen ruido.
  const noisy = [];
  for (const e of s.enemies.values()) if (!same(positionsBefore.get(`enemy:${e.enemyId}`), e.position) || e.telegraph.length) noisy.push(e.position);
  for (const a of s.allies) if (a.position && a.state !== 'EMBER_SPIRIT' && !same(positionsBefore.get(a.playerId), a.position)) noisy.push(a.position);
  const seen = new Set();
  s.heroes.noise = noisy.filter((p) => { const k = key(p.x, p.y); if (seen.has(k)) return false; seen.add(k); return true; });
}

/** Avaro: el daño sale de sus ascuas y cae al suelo. Con 0 y otro golpe, cae. Devuelve el daño hecho. */
export function miserDamage(s, hero, dmg, events) {
  if (hero.embers <= 0) {
    hero.hp = 0;
    hero.state = 'DOWN';
    hero.downRoundsLeft = 3;
    events.push({ type: 'DOWNED', targetId: hero.playerId, _at: hero.position });
    return dmg;
  }
  const lost = Math.min(hero.embers, dmg);
  hero.embers -= lost;
  const pile = s.heroes.ground.find((g) => same(g.position, hero.position));
  if (pile) pile.amount += lost; else s.heroes.ground.push({ position: clone(hero.position), amount: lost });
  events.push({ type: 'EMBERS_DROPPED', position: clone(hero.position), amount: lost });
  if (hero === s.me) events.push({ type: 'EMBERS_LOST', actorId: hero.playerId, amount: -lost, detail: 'HIT' });
  return dmg;
}

/** Golpe de un anuncio desviado: daña lo que haya en sus casillas; a un aliado, en secreto. */
export function correctedStrike(s, e, events) {
  const by = e.correctedBy;
  e.correctedBy = null;
  for (const other of [...s.enemies.values()]) {
    if (other !== e && e.telegraph.some((p) => same(p, other.position))) s.hit(other, 1, events, by);
    if (other === e && e.telegraph.some((p) => same(p, e.position))) s.hit(e, 1, events, by);
  }
  for (const a of s.allies) {
    if (a.state === 'ALIVE' && e.telegraph.some((p) => same(p, a.position))) {
      s.damageHero(a, 1, `enemy:${e.enemyId}`, events);
      noteBetrayal(s, by, a, 1, events, by);
    }
  }
}

/** Traiciones sumadas por traidor y víctima, para MatchFinished. Si no hubo, Polizón aliado se llevó a alguien por delante. */
export function finalBetrayals(s) {
  const out = [...s.heroes.betrayals].map(([k, damage]) => {
    const [traitorId, victimId] = k.split('>');
    return { traitorId, victimId, damage };
  });
  const stowaway = s.allies.find((a) => a.hero === 'POLIZON');
  if (!out.length && stowaway && s.round > 0) out.push({ traitorId: stowaway.playerId, victimId: s.me.playerId, damage: 1 });
  return out;
}
