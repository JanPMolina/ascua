// Arte del tablero generado por código (la CSP no deja cargar imágenes externas): sprites pixel-art
// de 16×16 definidos como matrices de caracteres, con contorno automático, y baldosas pintadas con
// un azar determinista por casilla. Todo se dibuja una vez en canvas fuera de pantalla y se reutiliza.

const OUTLINE = '#120c0a';

// ───────────── sprites de personajes ─────────────
// Cada fila tiene 16 columnas; '.' es transparente. La paleta es propia de cada sprite.

const ART = {
  YUNQUE: {
    palette: { h: '#b8c6d4', H: '#6f8399', e: '#ffe9a8', a: '#8ea3b8', A: '#5b6f86', S: '#7a5a2e', G: '#e0b546', w: '#c9ced6', t: '#6b4a2a', b: '#3d4654' },
    rows: [
      '................',
      '.....hhhhhh.....',
      '....hhhhhhhh....',
      '....hHHHHHHh....',
      '....hHeHHeHh....',
      '....hhHHHHhh....',
      '..SSSaaaaaaA.ww.',
      '.SSGSSaaaaaAwwww',
      '.SGGGSaAAAaAwwww',
      '.SSGSSaaaaaA.t..',
      '.SSSSSaAAAaA.t..',
      '..SSSaaaaaaA.t..',
      '....aaAa.aAa.t..',
      '....bbb..bbb....',
      '...bbbb..bbbb...',
      '................',
    ],
  },
  CHISPA: {
    palette: { h: '#f2a14a', H: '#9c3f17', s: '#f1c49a', E: '#2a1a14', r: '#e8873a', R: '#b5561f', y: '#fff3b0', Y: '#ffcf4a', t: '#7a4e2c', b: '#4a2c1c' },
    rows: [
      '.......hh.......',
      '......hhh.......',
      '.....hhhh....y..',
      '.....hhhhh..yYy.',
      '....hhhhhhh..y..',
      '...HHHHHHHHH.t..',
      '.....sEssEs..t..',
      '.....sssss...t..',
      '....rrrrrrr.st..',
      '...rrRrrrRrr.t..',
      '...rrrrrrrrr.t..',
      '...rRrrrrrRr.t..',
      '...rrrrrrrrr.t..',
      '....rrrrrrr..t..',
      '.....bb.bb......',
      '................',
    ],
  },
  CUERVO: {
    palette: { c: '#7656b4', C: '#4a3478', m: '#241c2c', E: '#c9f0ff', B: '#d8c8a0', k: '#dfe6ee', d: '#2a2030' },
    rows: [
      '.......cc.......',
      '......cccc......',
      '.....cccccc.....',
      '....ccCCCCcc....',
      '....cCmmmmCc....',
      '....cmEmmEmc....',
      '....cmmBBmmc....',
      '...ccCmBBmCcc...',
      '..cccCCBBCCccc..',
      '.cccccCccCccccc.',
      '.cc.ccCccCcc.cck',
      '.c..cccccccc..k.',
      '....cCccccCc....',
      '....cc....cc....',
      '....dd....dd....',
      '................',
    ],
  },
  RAIZ: {
    palette: { L: '#9ad46a', l: '#5e9e3c', g: '#6fbf5a', s: '#d8b48a', E: '#1e2a18', F: '#ff9ad0', v: '#4f8f4a', V: '#356b35', T: '#8a5a32' },
    rows: [
      '.L............L.',
      '.LL...gggg...LL.',
      '..lL.gggggg.Ll..',
      '...lggggggggl...',
      '....gsEssEsg....',
      '....gssssssg..LL',
      '.....ssFsss..lL.',
      '....vvvvvvvv.T..',
      '...vvvvvvvvvvT..',
      '...vVvvvvvvVsT..',
      '...vvvvVVvvv.T..',
      '....vVvvvvVv.T..',
      '....vvvvvvvv.T..',
      '.....vv..vv..T..',
      '.....ll..ll.....',
      '................',
    ],
  },
  CARRONERO: {
    palette: { r: '#a5604c', R: '#6e3a30', Y: '#ffd25a', W: '#f0e8d0', c: '#d8c8a8' },
    rows: [
      '................',
      '................',
      '....rrrrr.......',
      '...rrrrrrr......',
      '..rrYrrrYrr.....',
      '..rrrrrrrrr.....',
      '..rWrWrWrWr.....',
      '...rrrrrrr..c...',
      '.rrrRRrrrrrrcc..',
      'rrrRRRRrrrrr....',
      'rr.rRRRrrr.rr...',
      'c..rrrrrrr...c..',
      '...rr...rr......',
      '..cc.....cc.....',
      '................',
      '................',
    ],
  },
  POLILLA: {
    palette: { w: '#dccbe8', W: '#9a7fc0', b: '#6a5a4a', Y: '#ffe36a', a: '#c8b8a0' },
    rows: [
      '................',
      '.....a....a.....',
      '......a..a......',
      '.ww....bb....ww.',
      'wwww..bYYb..wwww',
      'wWWww.bbbb.wwWWw',
      'wWWwwwbbbbwwwWWw',
      'wwwwwwbbbbwwwwww',
      '.wwwwwbbbbwwwww.',
      '..wwWwbbbbwWww..',
      '..wWWwwbbwwWWw..',
      '...www.bb.www...',
      '................',
      '................',
      '................',
      '................',
    ],
  },
  GELATINA: {
    palette: { j: '#a6d05a', J: '#6a9a36', h: '#efffc8', E: '#1a2a18' },
    rows: [
      '................',
      '................',
      '................',
      '................',
      '......jjjj......',
      '....jjjjjjjj....',
      '...jjhhjjjjjj...',
      '..jjhhjjjjjjjj..',
      '..jjjEjjjjEjjj..',
      '.jjjjjjjjjjjjjj.',
      '.jjjjjJJJJjjjjj.',
      '.jjjjjjjjjjjjJj.',
      '.JjjjjjjjjjjjJJ.',
      '..JJJJJJJJJJJJ..',
      '................',
      '................',
    ],
  },
  SALTASAPO: {
    palette: { f: '#8aa83a', F: '#5a7424', y: '#f0e070', E: '#ffef6a', P: '#1a1a10', m: '#3a2010' },
    rows: [
      '................',
      '................',
      '...EE......EE...',
      '..EPEE....EEPE..',
      '..EEEff..ffEEE..',
      '...ffffffffff...',
      '..ffffffffffff..',
      '..fmmmmmmmmmmf..',
      '.ffffffffffffff.',
      '.ffFyyyyyyyyFff.',
      'ffFFyyyyyyyyFFff',
      'fF..FyyyyyyF..Ff',
      'FF..FFf..fFF..FF',
      '................',
      '................',
      '................',
    ],
  },
  BALLESTERO: {
    palette: { o: '#e8e0cc', O: '#a8a090', E: '#ff4a3a', x: '#7a5a3a', X: '#c0c8d0' },
    rows: [
      '................',
      '.....oooooo.....',
      '....oooooooo....',
      '....oEEooEEo....',
      '....oooooooo....',
      '.....oOoOoO.....',
      '......oooo......',
      '...xxxxxxxxxxX..',
      '..XxOooooooOxX..',
      '....oOoOoOoo....',
      '....ooOoOoOo....',
      '.....oooooo.....',
      '.....o.O..o.....',
      '....oo....oo....',
      '....OO....OO....',
      '................',
    ],
  },
  TOTEM: {
    palette: { g: '#d8b84a', t: '#8a5a32', T: '#5a3a20', E: '#ff6a2a', w: '#c84a3a', W: '#f0e8d0' },
    rows: [
      '.......gg.......',
      '......gggg......',
      '.ww..tttttt..ww.',
      '.wwwtEttttEtwww.',
      '..wwttTTTTttww..',
      '....tWWWWWWt....',
      '....tttttttt....',
      '....tEttttEt....',
      '....tttTTttt....',
      '....ttTTTTtt....',
      '....tttttttt....',
      '....tTtttTtt....',
      '....tttttttt....',
      '...TTTTTTTTTT...',
      '..TTTTTTTTTTTT..',
      '................',
    ],
  },
  CELADOR: {
    palette: { h: '#d8b04a', a: '#6a2228', A: '#3a1014', E: '#ff3a2a', r: '#a8323a' },
    rows: [
      '.h............h.',
      '.hh..aaaaaa..hh.',
      '..hhaaaaaaaahh..',
      '...aAAAAAAAAa...',
      '...aAEAAAAEAa...',
      '...aaAAAAAAaa...',
      '.rraaaaAAaaaarr.',
      'rrraaAaAAaAaarrr',
      'rrraaaaAAaaaarrr',
      'rr.aAaaAAaaAa.rr',
      'rr.aaaaAAaaaa.rr',
      'r..aAaaaaaaAa..r',
      '...aaaa..aaaa...',
      '...AAA....AAA...',
      '..AAAA....AAAA..',
      '................',
    ],
  },
  // Objetos del tablero.
  CHEST: {
    palette: { w: '#8a5a2e', W: '#5e3a1c', g: '#e0b546', G: '#a8802a', k: '#2a1a10' },
    rows: [
      '................',
      '................',
      '................',
      '................',
      '...gWWWWWWWWg...',
      '..gwwwwwwwwwwg..',
      '..gwwwwwwwwwwg..',
      '..gggggGGggggg..',
      '..gWWWWgkgWWWg..',
      '..gwwwwGGwwwwg..',
      '..gwwwwwwwwwwg..',
      '..gwwwwwwwwwwg..',
      '..gWWWWWWWWWWg..',
      '..gggggggggggg..',
      '................',
      '................',
    ],
  },
  CHEST_OPENED: {
    palette: { w: '#7a5028', W: '#4e3018', g: '#b8963c', G: '#8a6a24', I: '#1e0e06', y: '#ffe070', Y: '#c89a30' },
    rows: [
      '................',
      '...gWWWWWWWWg...',
      '..gwwwwwwwwwwg..',
      '..gwwwwwwwwwwg..',
      '..gggggggggggg..',
      '..gIIIIIIIIIIg..',
      '..gIIyIIIIYIIg..',
      '..gggggGGggggg..',
      '..gWWWWgggWWWg..',
      '..gwwwwGGwwwwg..',
      '..gwwwwwwwwwwg..',
      '..gwwwwwwwwwwg..',
      '..gWWWWWWWWWWg..',
      '..gggggggggggg..',
      '................',
      '................',
    ],
  },
  BRAZIER: {
    palette: { i: '#5a5a64', I: '#2e2e36', c: '#ff8a3a', C: '#c03a1a' },
    rows: [
      '................',
      '................',
      '................',
      '................',
      '................',
      '................',
      '................',
      '..iiiiiiiiiiii..',
      '..IccCccCccCcI..',
      '...IiiiiiiiiI...',
      '....IiiiiiiI....',
      '......IiiI......',
      '......iIIi......',
      '.....ii..ii.....',
      '....II....II....',
      '................',
    ],
  },
  ALTAR: {
    palette: { s: '#9a9ea6', S: '#62666e', R: '#6ae0e0' },
    rows: [
      '................',
      '................',
      '................',
      '................',
      '................',
      '...ssssssssss...',
      '..ssssRssRssss..',
      '..SSSSSSSSSSSS..',
      '...sssssssssss..',
      '...ssRsssssRss..',
      '...sssRRRRssss..',
      '...ssssRRsssss..',
      '...ssssssssss...',
      '..SSSSSSSSSSSS..',
      '..SSSSSSSSSSSS..',
      '................',
    ],
  },
};

/** Héroes: color de equipo (anillo, nombre, flecha de mirada). */
export const HERO_COLORS = { YUNQUE: '#a9c1d9', CHISPA: '#f5a65b', CUERVO: '#b39be6', RAIZ: '#7fd69a' };

const hexToRgb = (hex) => [parseInt(hex.slice(1, 3), 16), parseInt(hex.slice(3, 5), 16), parseInt(hex.slice(5, 7), 16)];
const rgbToHex = (r, g, b) => `#${[r, g, b].map((v) => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, '0')).join('')}`;

const VARIANTS = {
  base: (c) => c,
  white: () => '#ffffff',
  ice: (c) => { const [r, g, b] = hexToRgb(c); return rgbToHex(r * 0.35 + 150, g * 0.35 + 205, b * 0.3 + 235); },
  gray: (c) => { const [r, g, b] = hexToRgb(c); const l = (r * 0.3 + g * 0.59 + b * 0.11) * 0.62; return rgbToHex(l, l, l * 1.08); },
};

function canvas(w, h) {
  const c = document.createElement('canvas');
  c.width = Math.max(1, Math.ceil(w));
  c.height = Math.max(1, Math.ceil(h));
  return c;
}

const spriteCache = new Map();

/**
 * Sprite listo para dibujar: { base, white, ice, gray, size, pixels }. Los canvas miden 18×18 (16 + contorno);
 * `pixels` ([x, y, color]) sirve para deshacer el sprite en partículas al morir.
 */
export function sprite(kind) {
  if (spriteCache.has(kind)) return spriteCache.get(kind);
  const art = ART[kind];
  if (!art) return null;
  const W = 16;
  const grid = art.rows.map((row) => row.padEnd(W, '.').slice(0, W));
  const at = (x, y) => (x >= 0 && y >= 0 && x < W && y < grid.length ? grid[y][x] : '.');
  const out = { size: W + 2, pixels: [] };
  for (const [name, fn] of Object.entries(VARIANTS)) {
    const c = canvas(W + 2, W + 2);
    const g = c.getContext('2d');
    for (let y = -1; y <= grid.length; y++) {
      for (let x = -1; x <= W; x++) {
        const ch = at(x, y);
        let color = null;
        if (ch !== '.') color = art.palette[ch] ?? '#ff00ff';
        else if (at(x - 1, y) !== '.' || at(x + 1, y) !== '.' || at(x, y - 1) !== '.' || at(x, y + 1) !== '.') color = OUTLINE;
        if (!color) continue;
        g.fillStyle = name === 'white' ? '#ffffff' : color === OUTLINE && name !== 'gray' ? OUTLINE : fn(color);
        g.fillRect(x + 1, y + 1, 1, 1);
        if (name === 'base' && ch !== '.') out.pixels.push([x + 1, y + 1, color]);
      }
    }
    out[name] = c;
  }
  spriteCache.set(kind, out);
  return out;
}

// ───────────── brillo radial (luces, auras) ─────────────

const glowCache = new Map();

/** Disco radial suave de 128 px, blanco o de un color; se escala al dibujar. */
export function glow(color = '#ffffff', hardness = 0.35) {
  const key = `${color}|${hardness}`;
  if (glowCache.has(key)) return glowCache.get(key);
  const c = canvas(128, 128);
  const g = c.getContext('2d');
  const grad = g.createRadialGradient(64, 64, 0, 64, 64, 64);
  const [r, gr, b] = hexToRgb(color);
  grad.addColorStop(0, `rgba(${r},${gr},${b},1)`);
  grad.addColorStop(hardness, `rgba(${r},${gr},${b},0.85)`);
  grad.addColorStop(1, `rgba(${r},${gr},${b},0)`);
  g.fillStyle = grad;
  g.fillRect(0, 0, 128, 128);
  glowCache.set(key, c);
  return c;
}

/** Ícono de peligro (triángulo con «!») para los ataques anunciados, al tamaño pedido. */
export function dangerIcon(px) {
  const key = `danger|${px}`;
  if (glowCache.has(key)) return glowCache.get(key);
  const s = Math.max(8, Math.round(px));
  const c = canvas(s, s);
  const g = c.getContext('2d');
  g.beginPath();
  g.moveTo(s / 2, s * 0.06);
  g.lineTo(s * 0.96, s * 0.9);
  g.lineTo(s * 0.04, s * 0.9);
  g.closePath();
  g.fillStyle = '#ffcf4a';
  g.fill();
  g.lineWidth = Math.max(1, s * 0.08);
  g.strokeStyle = '#3a0a06';
  g.stroke();
  g.fillStyle = '#3a0a06';
  g.fillRect(s * 0.44, s * 0.32, s * 0.12, s * 0.32);
  g.fillRect(s * 0.44, s * 0.7, s * 0.12, s * 0.11);
  glowCache.set(key, c);
  return c;
}

// ───────────── baldosas ─────────────

/** Azar determinista por casilla: la misma baldosa se ve igual en cada redibujo. */
export function seeded(x, y, salt = 0) {
  let s = (x * 73856093) ^ (y * 19349663) ^ (salt * 83492791) ^ 0x5bd1e995;
  return () => {
    s ^= s << 13; s ^= s >>> 17; s ^= s << 5;
    return ((s >>> 0) % 10000) / 10000;
  };
}

const FLOOR_TONES = ['#5a4836', '#554433', '#5e4c39', '#514131'];

/**
 * Pinta la base estática de una casilla (suelo, muro con relieve, grieta, placa de pinchos, escalera…)
 * en `g`, en la celda (x, y) de `cell` píxeles. Los objetos animados (fuego, cofres, brasero) van aparte.
 * `isWall(x, y)` permite dar relieve y sombra según los vecinos.
 */
export function paintTile(g, kind, x, y, cell, isWall) {
  const rnd = seeded(x, y);
  const p = cell / 16;
  const ox = x * cell;
  const oy = y * cell;
  const px = (cx, cy, w, h, color) => { g.fillStyle = color; g.fillRect(ox + cx * p, oy + cy * p, w * p, h * p); };

  if (kind === 'WALL') {
    const front = !isWall(x, y + 1);
    const topH = front ? 10 : 16;
    px(0, 0, 16, topH, '#776c62');
    // Losas del techo del muro con bisel.
    for (let i = 0; i < 4; i++) {
      const sx = Math.floor(rnd() * 12);
      const sy = Math.floor(rnd() * (topH - 3));
      px(sx, sy, 3 + Math.floor(rnd() * 3), 1, '#867a6e');
    }
    px(0, 0, 16, 1, '#968a7c');
    px(0, 0, 1, topH, '#8a7e72');
    if (!isWall(x + 1, y)) px(15, 0, 1, topH, '#564c43');
    if (!isWall(x, y - 1)) px(0, 0, 16, 1, '#a89a8a');
    if (front) {
      // Cara frontal de ladrillo: da altura al muro.
      px(0, 10, 16, 6, '#4e443b');
      px(0, 10, 16, 1, '#2a2420');
      const shift = (x + y) % 2 ? 0 : 4;
      for (let row = 0; row < 2; row++) {
        const yy = 11 + row * 3;
        px(0, yy + 2, 16, 1, '#2d2723');
        for (let bx = (row ? shift + 4 : shift) % 8; bx < 16; bx += 8) px(bx, yy, 1, 2, '#2d2723');
      }
      px(0, 15, 16, 1, '#1c1815');
    }
    return;
  }

  // Suelo de losas para todo lo demás.
  px(0, 0, 16, 16, FLOOR_TONES[Math.floor(rnd() * FLOOR_TONES.length)]);
  const split = 6 + Math.floor(rnd() * 4);
  px(0, split, 16, 1, '#382c22');
  px(Math.floor(rnd() * 14) + 1, 0, 1, split, '#382c22');
  px(Math.floor(rnd() * 14) + 1, split + 1, 1, 15 - split, '#382c22');
  px(0, 0, 16, 1, '#3c3026');
  px(0, 0, 1, 16, '#3c3026');
  for (let i = 0; i < 7; i++) px(Math.floor(rnd() * 16), Math.floor(rnd() * 16), 1, 1, rnd() < 0.5 ? '#6a5642' : '#43362a');
  if (rnd() < 0.18) {
    // Musgo o huesos sueltos de vez en cuando.
    const mx = 2 + Math.floor(rnd() * 10);
    const my = 2 + Math.floor(rnd() * 10);
    if (rnd() < 0.6) { px(mx, my, 3, 1, '#3f5a34'); px(mx + 1, my + 1, 2, 1, '#35502c'); } else { px(mx, my, 3, 1, '#b8ad96'); px(mx + 1, my - 1, 1, 3, '#b8ad96'); }
  }
  // Sombra que proyecta un muro de arriba o de la izquierda.
  if (isWall(x, y - 1)) { px(0, 0, 16, 2, 'rgba(0,0,0,0.45)'); px(0, 2, 16, 1, 'rgba(0,0,0,0.2)'); }
  if (isWall(x - 1, y)) px(0, 0, 2, 16, 'rgba(0,0,0,0.3)');

  switch (kind) {
    case 'CRACK': {
      // Grieta: un abismo dentado.
      g.fillStyle = '#2e1f3e';
      g.beginPath();
      const pts = [[3, 4], [7, 2], [10, 4], [13, 3], [14, 8], [12, 12], [8, 14], [5, 12], [2, 9]];
      pts.forEach(([a, b], i) => { const jx = (rnd() - 0.5) * 1.5; const jy = (rnd() - 0.5) * 1.5; (i ? g.lineTo : g.moveTo).call(g, ox + (a + jx) * p, oy + (b + jy) * p); });
      g.closePath();
      g.fill();
      g.fillStyle = '#07050a';
      g.beginPath();
      pts.forEach(([a, b], i) => (i ? g.lineTo : g.moveTo).call(g, ox + (8 + (a - 8) * 0.72) * p, oy + (8 + (b - 8) * 0.72 + 0.6) * p));
      g.closePath();
      g.fill();
      break;
    }
    case 'SPIKES':
    case 'SPIKES_ARMED':
      px(2, 2, 12, 12, '#4a4650');
      px(2, 2, 12, 1, '#625e6a');
      px(2, 13, 12, 1, '#2e2b33');
      for (const [hx, hy] of [[4, 4], [10, 4], [4, 10], [10, 10], [7, 7]]) px(hx, hy, 2, 2, '#16141a');
      break;
    case 'FIRE':
      px(1, 2, 14, 12, 'rgba(20,10,6,0.55)');
      px(3, 4, 10, 9, 'rgba(10,4,2,0.5)');
      break;
    case 'STAIRS':
      px(1, 1, 14, 14, '#1a1712');
      for (let i = 0; i < 5; i++) {
        const shade = ['#6a6058', '#564d46', '#433b35', '#302a25', '#211d19'][i];
        px(2 + i, 2 + i * 2.4, 12 - i * 2, 2, shade);
        px(2 + i, 2 + i * 2.4, 12 - i * 2, 0.5, '#7d746a');
      }
      break;
    case 'ALTAR':
    case 'BRAZIER':
    case 'CHEST':
    case 'CHEST_OPENED':
      px(2, 12, 12, 3, 'rgba(0,0,0,0.35)');
      break;
    default:
      break;
  }
}

/** Copia desaturada y oscurecida de un canvas (casillas recordadas fuera de la visión). */
export function grayCopy(src, target = null) {
  const c = target ?? canvas(src.width, src.height);
  if (c.width !== src.width || c.height !== src.height) { c.width = src.width; c.height = src.height; }
  const g = c.getContext('2d', { willReadFrequently: true });
  g.clearRect(0, 0, c.width, c.height);
  g.drawImage(src, 0, 0);
  const img = g.getImageData(0, 0, c.width, c.height);
  const d = img.data;
  for (let i = 0; i < d.length; i += 4) {
    const l = (d[i] * 0.3 + d[i + 1] * 0.59 + d[i + 2] * 0.11) * 0.58;
    d[i] = l * 0.92;
    d[i + 1] = l * 0.96;
    d[i + 2] = l * 1.12;
  }
  g.putImageData(img, 0, 0);
  return c;
}

export { canvas as makeCanvas };
