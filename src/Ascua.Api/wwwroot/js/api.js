// Cliente REST. Todas las rutas están en ROUTES para ajustarlas en un solo lugar.
// Sesión: el access token y el refresh token viven SOLO en memoria (nunca en localStorage),
// así que un XSS no los puede leer de un almacenamiento persistente y recargar = volver a entrar.

export const ROUTES = {
  register: '/api/v1/auth/register',
  login: '/api/v1/auth/login',
  refresh: '/api/v1/auth/refresh',
  logout: '/api/v1/auth/logout',
  me: '/api/v1/users/me',
  // Salas y emparejamiento (las implementa otra rama; si cambian, se ajustan aquí).
  rooms: '/api/v1/rooms',
  room: (roomId) => `/api/v1/rooms/${encodeURIComponent(roomId)}`,
  roomCurrent: '/api/v1/rooms/current',
  roomJoinByCode: '/api/v1/rooms/join',
  roomJoin: (roomId) => `/api/v1/rooms/${encodeURIComponent(roomId)}/join`,
  roomHero: (roomId) => `/api/v1/rooms/${encodeURIComponent(roomId)}/hero`,
  roomReady: (roomId) => `/api/v1/rooms/${encodeURIComponent(roomId)}/ready`,
  roomStart: (roomId) => `/api/v1/rooms/${encodeURIComponent(roomId)}/start`,
  roomLeave: (roomId) => `/api/v1/rooms/${encodeURIComponent(roomId)}/leave`,
  queue: '/api/v1/matchmaking/queue',
  // Tiempo real (ADR-0006).
  tickets: '/api/v1/realtime/tickets',
};

/** Error HTTP con el Problem Details (RFC 9457) de la API. */
export class ApiError extends Error {
  constructor(status, problem) {
    super(describeProblem(status, problem));
    this.status = status;
    this.code = problem?.code ?? null;
    this.problem = problem ?? null;
  }
}

function describeProblem(status, problem) {
  if (!problem) return `Error HTTP ${status}`;
  const fieldErrors = problem.errors
    ? Object.entries(problem.errors).map(([field, msgs]) => `${field}: ${[].concat(msgs).join(' ')}`).join(' · ')
    : '';
  return [problem.title ?? `Error HTTP ${status}`, problem.detail, fieldErrors].filter(Boolean).join(' — ');
}

// ───────────── Sesión en memoria ─────────────

const session = {
  accessToken: null,
  refreshToken: null,
  accessExpiresAt: 0,
  username: null,
  refreshTimer: 0,
  refreshing: null,
};

const listeners = { change: new Set() };

/** Se llama con `true` al iniciar sesión y con `false` al cerrarla o perderla. */
export function onSessionChange(fn) { listeners.change.add(fn); }

export const isLoggedIn = () => session.accessToken !== null;
export const currentUsername = () => session.username;

function setTokens(tokens) {
  session.accessToken = tokens.accessToken;
  session.refreshToken = tokens.refreshToken;
  session.accessExpiresAt = Date.now() + tokens.expiresIn * 1000;
  clearTimeout(session.refreshTimer);
  // Se renueva un minuto antes de que expire (o al 80% si la vida es corta).
  const lead = Math.min(60, tokens.expiresIn * 0.2);
  const delay = Math.max(5, tokens.expiresIn - lead) * 1000;
  session.refreshTimer = setTimeout(() => { refreshNow().catch(() => {}); }, delay);
}

function clearSession() {
  const wasLoggedIn = isLoggedIn();
  clearTimeout(session.refreshTimer);
  session.accessToken = null;
  session.refreshToken = null;
  session.accessExpiresAt = 0;
  session.username = null;
  if (wasLoggedIn) listeners.change.forEach((fn) => fn(false));
}

/** Rota el refresh token. Las llamadas concurrentes comparten la misma promesa (dos refresh
 *  simultáneos del mismo token contarían como reutilización y revocarían la familia). */
export function refreshNow() {
  if (session.refreshing) return session.refreshing;
  if (!session.refreshToken) return Promise.reject(new ApiError(401, null));
  session.refreshing = (async () => {
    try {
      const tokens = await send('POST', ROUTES.refresh, { refreshToken: session.refreshToken }, { auth: false });
      setTokens(tokens);
      return tokens;
    } catch (err) {
      if (err instanceof ApiError && (err.status === 401 || err.status === 403)) clearSession();
      throw err;
    } finally {
      session.refreshing = null;
    }
  })();
  return session.refreshing;
}

export async function register(username, email, password) {
  return send('POST', ROUTES.register, { username, email, password }, { auth: false });
}

export async function login(username, password) {
  const tokens = await send('POST', ROUTES.login, { username, password }, { auth: false });
  setTokens(tokens);
  try {
    const me = await send('GET', ROUTES.me);
    session.username = me.username;
  } catch {
    session.username = username;
  }
  listeners.change.forEach((fn) => fn(true));
}

export async function logout() {
  const refreshToken = session.refreshToken;
  clearSession();
  if (refreshToken) {
    await send('POST', ROUTES.logout, { refreshToken }, { auth: false }).catch(() => {});
  }
}

// ───────────── Transporte ─────────────

async function send(method, path, body, { auth = true, retried = false } = {}) {
  const headers = { Accept: 'application/json, application/problem+json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (auth && session.accessToken) headers.Authorization = `Bearer ${session.accessToken}`;

  const response = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: 'same-origin',
    cache: 'no-store',
  });

  // Token vencido (p. ej. la pestaña estuvo dormida): se renueva una vez y se reintenta.
  if (response.status === 401 && auth && !retried && session.refreshToken) {
    await refreshNow();
    return send(method, path, body, { auth, retried: true });
  }

  const text = await response.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); } catch { data = null; }
  }
  if (!response.ok) throw new ApiError(response.status, data);
  return data;
}

// ───────────── Salas y emparejamiento ─────────────

/** Acepta variaciones de nombre de campo para no romperse si el DTO final difiere un poco. */
export function normalizeRoom(raw) {
  if (!raw) return null;
  const members = raw.members ?? [];
  return {
    id: raw.id ?? raw.roomId,
    name: raw.name ?? '',
    code: raw.inviteCode ?? raw.code ?? null,
    visibility: raw.visibility ?? 'PUBLIC',
    hostName: raw.hostUsername ?? members.find((m) => m.isHost)?.username ?? '',
    status: raw.status ?? '',
    matchId: raw.matchId ?? null,
    capacity: raw.capacity ?? raw.maxPlayers ?? 4,
    playerCount: raw.players ?? raw.playerCount ?? members.length,
    members: members.map((m, i) => ({
      userId: m.userId ?? m.id,
      username: m.username ?? '',
      hero: m.hero ?? null,
      ready: Boolean(m.ready),
      seat: i + 1,
      isHost: Boolean(m.isHost),
    })),
  };
}

const unwrapList = (data) => (Array.isArray(data) ? data : data?.items ?? []);

export const listRooms = async () => unwrapList(await send('GET', `${ROUTES.rooms}?page=1&pageSize=20`)).map(normalizeRoom);
export const createRoom = async (name, visibility, maxPlayers = 4) =>
  normalizeRoom(await send('POST', ROUTES.rooms, { name, visibility, maxPlayers }));
export const currentRoom = async () => normalizeRoom(await send('GET', ROUTES.roomCurrent));
export const joinRoom = async (roomId) => normalizeRoom(await send('POST', ROUTES.roomJoin(roomId)));
export const joinRoomByCode = async (code) => normalizeRoom(await send('POST', ROUTES.roomJoinByCode, { code }));
export const getRoom = async (roomId) => normalizeRoom(await send('GET', ROUTES.room(roomId)));
export const setHero = async (roomId, hero) => normalizeRoom(await send('PUT', ROUTES.roomHero(roomId), { hero }));
export const setReady = async (roomId, ready) => normalizeRoom(await send('PUT', ROUTES.roomReady(roomId), { ready }));
export const leaveRoom = (roomId) => send('POST', ROUTES.roomLeave(roomId));

/** Inicia la partida (solo el anfitrión); devuelve el matchId. */
export async function startRoom(roomId) {
  const data = await send('POST', ROUTES.roomStart(roomId));
  return data?.matchId ?? null;
}

/** Estado de la cola: { status: IDLE|QUEUED|MATCHING|MATCHED|EXPIRED, position, playersInQueue, matchId }. */
export const enqueue = (preferredHero) => send('POST', ROUTES.queue, { preferredHero });
export const queueStatus = () => send('GET', ROUTES.queue);
export const dequeue = () => send('DELETE', ROUTES.queue);

/** Ticket de un solo uso (30 s) para abrir el WebSocket de una partida. */
export const createTicket = (matchId) => send('POST', ROUTES.tickets, { matchId });
