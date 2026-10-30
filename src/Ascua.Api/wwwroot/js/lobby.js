// Lobby: salas públicas, crear/unirse por código, héroe, listo, iniciar y cola rápida.
import * as api from './api.js';
import { $, busy, el, formData, setText, showScreen, toast } from './ui.js';
import { HEROES, label } from './labels.js';

const POLL_MS = 2000;
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

let enterMatch = () => {};
let pollTimer = 0;
let currentRoom = null;
let bound = false;

function heroOptions(select, selected) {
  const placeholder = selected ? [] : [el('option', { value: '', selected: true, disabled: true }, '— elige —')];
  select.replaceChildren(...placeholder, ...Object.entries(HEROES).map(([id, text]) =>
    el('option', { value: id, selected: id === selected }, text)));
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
    body.replaceChildren(...rooms.map((room) => el('tr', {},
      el('td', {}, room.name),
      el('td', {}, room.hostName),
      el('td', {}, `${room.playerCount}/${room.capacity}`),
      el('td', {}, el('button', { type: 'button', onclick: () => joinPublic(room.id) }, 'Unirse')))));
    $('rooms-empty').hidden = rooms.length > 0;
  } catch (err) {
    body.replaceChildren();
    $('rooms-empty').hidden = false;
    const missing = err instanceof api.ApiError && err.status === 404;
    setText('rooms-empty', missing
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
  setText('room-visibility', room.visibility === 'PRIVATE' ? 'privada' : 'pública');
  $('room-members').replaceChildren(...room.members.map((m) => el('tr', {},
    el('td', {}, String(m.seat)),
    el('td', {}, `${m.username}${m.isHost ? ' (anfitrión)' : ''}`),
    el('td', {}, m.hero ? label(HEROES, m.hero) : '—'),
    el('td', {}, m.ready ? '✔' : '…'))));
  const mine = me(room);
  const select = $('room-hero');
  if (document.activeElement !== select) heroOptions(select, mine?.hero ?? null);
  $('btn-ready').textContent = mine?.ready ? 'Ya no estoy listo' : 'Marcar listo';
  $('btn-start').hidden = Boolean(mine) && !mine.isHost && room.members.some((m) => m.isHost);
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
      setText('queue-status', `${phase} ${Math.round((Date.now() - started) / 1000)} s${position}${waiting}`);
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
  heroOptions($('form-queue').elements.hero, 'YUNQUE');

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
