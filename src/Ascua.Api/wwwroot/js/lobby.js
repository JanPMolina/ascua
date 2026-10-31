// Lobby: salas públicas, crear/unirse por código, héroe, listo, iniciar y cola rápida.
import * as api from './api.js';
import { $, busy, el, formData, setText, showScreen, toast } from './ui.js';
import { HEROES } from './labels.js';
import { heroAvatar, heroInfo } from './heroes.js';

const POLL_MS = 2000;
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

let enterMatch = () => {};
let pollTimer = 0;
let currentRoom = null;
let bound = false;
let shownSeats = new Map(); // userId → listo, para animar solo los asientos que cambian

/** Tarjetas de héroe con radios reales (teclado y lector de pantalla las tratan como un grupo de opciones). */
function heroPicker(fieldset, name, selected) {
  const cards = Object.keys(HEROES).map((id) => {
    const info = heroInfo(id);
    return el('label', { class: 'hero-card', dataset: { hero: id } },
      el('input', { type: 'radio', name, value: id, checked: id === selected }),
      heroAvatar(id, 'md'),
      el('span', { class: 'hc-name' }, info.name),
      el('span', { class: 'hc-role' }, info.role),
      el('span', { class: 'hc-stats' }, el('span', { class: 'hp' }, `♥ ${info.hp} de vida`), '·', el('span', {}, info.passive)),
      el('span', { class: 'hc-ability' }, el('b', {}, info.ability), `: ${info.abilityText}`),
      el('span', { class: 'hc-taken' }));
  });
  fieldset.append(...cards);
}

function stopPolling() {
  clearTimeout(pollTimer);
  pollTimer = 0;
}

function explain(err) {
  return err?.message ?? String(err);
}

// ───────────── Lobby ─────────────

export async function showLobby() {
  stopPolling();
  currentRoom = null;
  showScreen('lobby');
  refreshRooms();
  // Si el usuario ya estaba en una sala (p. ej. volvió a iniciar sesión), se retoma.
  try {
    const room = await api.currentRoom();
    if (room && !room.matchId && $('screen-lobby').hidden === false) showRoom(room);
  } catch {
    // 404 = no está en ninguna sala.
  }
}

async function refreshRooms() {
  const body = $('rooms-body');
  try {
    const rooms = await api.listRooms();
    body.replaceChildren(...rooms.map((room, i) => {
      const full = room.playerCount >= room.capacity;
      const card = el('div', { class: 'room-card' },
        el('span', { class: full ? 'room-flame full' : 'room-flame', 'aria-hidden': 'true' }, '🔥'),
        el('div', {},
          el('b', {}, room.name),
          el('small', {}, `Anfitrión: ${room.hostName || '—'} · ${room.playerCount}/${room.capacity}`,
            el('span', { class: 'seat-pips', 'aria-hidden': 'true' },
              ...Array.from({ length: room.capacity }, (_, n) => el('i', { class: n < room.playerCount ? 'on' : '' }))))),
        el('button', { type: 'button', class: full ? 'secondary' : 'primary', disabled: full, onclick: () => joinPublic(room.id) },
          full ? 'Llena' : 'Unirse'));
      card.style.animationDelay = `${i * 60}ms`;
      return card;
    }));
    setText('rooms-empty-text', 'No hay salas públicas abiertas. ¡Enciende la primera!');
    $('rooms-empty').hidden = rooms.length > 0;
  } catch (err) {
    body.replaceChildren();
    $('rooms-empty').hidden = false;
    const missing = err instanceof api.ApiError && err.status === 404;
    setText('rooms-empty-text', missing
      ? `El servidor todavía no expone ${api.ROUTES.rooms}.`
      : `No se pudieron listar las salas: ${explain(err)}`);
  }
}

async function joinPublic(roomId) {
  try {
    showRoom(await api.joinRoom(roomId));
  } catch (err) {
    toast(`No se pudo unir: ${explain(err)}`, 'bad');
  }
}

async function joinByCode(code) {
  try {
    showRoom(await api.joinRoomByCode(code.trim()));
  } catch (err) {
    toast(`No se pudo unir: ${explain(err)}`, 'bad');
  }
}

// ───────────── Sala ─────────────

function me(room) {
  const username = api.currentUsername()?.toLowerCase();
  return room.members.find((m) => m.username.toLowerCase() === username) ?? null;
}

function showRoom(room) {
  if (currentRoom?.id !== room.id) shownSeats = new Map();
  currentRoom = room;
  showScreen('room');
  renderRoom(room);
  stopPolling();
  const poll = async () => {
    if (!currentRoom) return;
    try {
      const fresh = await api.getRoom(currentRoom.id);
      if (!currentRoom) return;
      currentRoom = fresh;
      renderRoom(fresh);
      if (fresh.matchId) {
        enter(fresh.matchId);
        return;
      }
    } catch (err) {
      if (err instanceof api.ApiError && err.status === 404) {
        toast('La sala ya no existe.', 'bad');
        showLobby();
        return;
      }
    }
    pollTimer = setTimeout(poll, POLL_MS);
  };
  pollTimer = setTimeout(poll, POLL_MS);
}

function renderRoom(room) {
  setText('room-name', room.name);
  setText('room-code', room.code ?? '');
  $('room-code-line').hidden = !room.code;
  const priv = room.visibility === 'PRIVATE';
  setText('room-visibility', priv ? '🔒 privada' : 'pública');
  $('room-visibility').className = priv ? 'badge warn' : 'badge';
  const mine = me(room);
  const seats = Array.from({ length: Math.max(room.capacity, room.members.length) }, (_, i) => room.members[i] ?? null);
  $('room-members').replaceChildren(...seats.map((m, i) => {
    if (!m) {
      return el('div', { class: 'seat empty' },
        el('span', { class: 'seat-n' }, `#${i + 1}`),
        heroAvatar(null, 'lg'),
        el('b', {}, 'Asiento libre'),
        el('small', {}, 'Esperando portador…'));
    }
    const info = m.hero ? heroInfo(m.hero) : null;
    const before = shownSeats.get(m.userId);
    const classes = ['seat', m === mine ? 'mine' : '', m.ready ? 'ready' : '',
      before === undefined ? 'enter' : '', m.ready && before === false ? 'just-ready' : ''].filter(Boolean).join(' ');
    return el('div', { class: classes, dataset: m.hero ? { hero: m.hero } : {} },
      el('span', { class: 'seat-n' }, `#${m.seat}`),
      m.isHost ? el('span', { class: 'host', title: 'Anfitrión' }, '♛') : null,
      heroAvatar(m.hero, 'lg'),
      el('b', {}, `${m.username}${m === mine ? ' (tú)' : ''}`),
      el('small', {}, info ? `${info.name} · ${info.role}` : 'Sin héroe'),
      el('span', { class: 'ready-state' }, m.ready ? '✔ Listo' : '… preparando'));
  }));
  shownSeats = new Map(room.members.map((m) => [m.userId, m.ready]));
  // Las tarjetas de héroe no se recrean (conservan el foco del teclado): solo se marca la elegida y quién más la tiene.
  for (const input of $('room-hero').querySelectorAll('input')) {
    input.checked = input.value === mine?.hero;
    const others = room.members.filter((m) => m !== mine && m.hero === input.value).map((m) => m.username);
    input.closest('.hero-card').querySelector('.hc-taken').textContent = others.length ? `También: ${others.join(', ')}` : '';
  }
  const ready = Boolean(mine?.ready);
  $('btn-ready').textContent = ready ? '✔ Listo (cancelar)' : 'Marcar listo';
  $('btn-ready').classList.toggle('is-ready', ready);
  $('btn-ready').setAttribute('aria-pressed', String(ready));
  $('btn-start').hidden = Boolean(mine) && !mine.isHost && room.members.some((m) => m.isHost);
  const allReady = room.members.length > 0 && room.members.every((m) => m.ready);
  $('btn-start').classList.toggle('primary', allReady);
}

function enter(matchId) {
  stopPolling();
  currentRoom = null;
  enterMatch(matchId);
}

// ───────────── Cola ─────────────

function startQueuePolling() {
  showScreen('queue');
  const started = Date.now();
  stopPolling();
  const poll = async () => {
    try {
      const status = await api.queueStatus();
      if (status?.matchId) {
        enter(status.matchId);
        return;
      }
      if (status?.status === 'IDLE' || status?.status === 'EXPIRED') {
        toast(status.status === 'EXPIRED' ? 'La búsqueda expiró.' : 'Ya no estás en la cola.', 'bad');
        showLobby();
        return;
      }
      const position = status?.position ? ` · posición ${status.position}` : '';
      const waiting = status?.playersInQueue ? ` · ${status.playersInQueue} en cola` : '';
      const phase = status?.status === 'MATCHING' ? 'Grupo formado; creando la partida…' : 'Esperando jugadores…';
      const secs = Math.round((Date.now() - started) / 1000);
      setText('queue-status', `${phase} ${Math.floor(secs / 60)}:${String(secs % 60).padStart(2, '0')}${position}${waiting}`);
    } catch (err) {
      setText('queue-status', `Error consultando la cola: ${explain(err)}`);
    }
    pollTimer = setTimeout(poll, POLL_MS);
  };
  poll();
}

// ───────────── Enlaces de la UI ─────────────

export function initLobby({ onEnterMatch }) {
  enterMatch = onEnterMatch;
  if (bound) return;
  bound = true;
  heroPicker($('queue-heroes'), 'hero', 'YUNQUE');
  heroPicker($('room-hero'), 'room-hero', null);

  $('btn-copy-code').addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText($('room-code').textContent);
      toast('Código copiado.', 'ok', 2000);
    } catch {
      toast('No se pudo copiar; selecciona el código a mano.', 'bad');
    }
  });

  $('btn-rooms-refresh').addEventListener('click', refreshRooms);

  $('form-create-room').addEventListener('submit', (e) => {
    e.preventDefault();
    const form = e.currentTarget;
    busy(form, async () => {
      try {
        const name = form.elements.name.value.trim();
        showRoom(await api.createRoom(name, form.elements.private.checked ? 'PRIVATE' : 'PUBLIC'));
      } catch (err) {
        toast(`No se pudo crear la sala: ${explain(err)}`, 'bad');
      }
    });
  });

  $('form-join-code').addEventListener('submit', (e) => {
    e.preventDefault();
    const form = e.currentTarget;
    busy(form, () => joinByCode(formData(form).code));
  });

  $('form-queue').addEventListener('submit', (e) => {
    e.preventDefault();
    const form = e.currentTarget;
    busy(form, async () => {
      try {
        const res = await api.enqueue(formData(form).hero);
        if (res?.matchId) enter(res.matchId); else startQueuePolling();
      } catch (err) {
        toast(`No se pudo entrar a la cola: ${explain(err)}`, 'bad');
      }
    });
  });

  $('btn-queue-cancel').addEventListener('click', async () => {
    stopPolling();
    await api.dequeue().catch(() => {});
    showLobby();
  });

  $('form-match-id').addEventListener('submit', (e) => {
    e.preventDefault();
    const { matchId } = formData(e.currentTarget);
    if (!UUID_RE.test(matchId.trim())) {
      toast('El matchId debe ser un UUID.', 'bad');
      return;
    }
    enter(matchId.trim().toLowerCase());
  });

  $('room-hero').addEventListener('change', async (e) => {
    if (!currentRoom) return;
    try {
      renderRoom(currentRoom = await api.setHero(currentRoom.id, e.target.value) ?? currentRoom);
    } catch (err) {
      renderRoom(currentRoom);
      toast(`No se pudo elegir héroe: ${explain(err)}`, 'bad');
    }
  });

  $('btn-ready').addEventListener('click', async () => {
    if (!currentRoom) return;
    const ready = !me(currentRoom)?.ready;
    try {
      renderRoom(currentRoom = await api.setReady(currentRoom.id, ready) ?? currentRoom);
    } catch (err) {
      toast(`No se pudo marcar listo: ${explain(err)}`, 'bad');
    }
  });

  $('btn-start').addEventListener('click', async () => {
    if (!currentRoom) return;
    try {
      const matchId = await api.startRoom(currentRoom.id);
      if (matchId) enter(matchId);
    } catch (err) {
      toast(`No se pudo iniciar: ${explain(err)}`, 'bad');
    }
  });

  $('btn-leave-room').addEventListener('click', async () => {
    const room = currentRoom;
    stopPolling();
    currentRoom = null;
    if (room) await api.leaveRoom(room.id).catch(() => {});
    showLobby();
  });
}

export function stopLobby() {
  stopPolling();
  currentRoom = null;
}
