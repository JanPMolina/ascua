// Transporte real: SignalR sobre WebSocket con MessagePack (contracts/asyncapi.yaml §1).
// La reconexión NO usa withAutomaticReconnect: cada intento necesita un ticket nuevo y debe
// respetar la razón de cierre (MATCH_FINISHED, SESSION_REPLACED… no reconectan). La decide match.js.
import { createTicket } from './api.js';

export const PROTOCOL_VERSION = 3;
export const HUB_PATH = '/realtime/match';

/** Mensajes que el servidor empuja (operaciones `send` del contrato). */
export const SERVER_MESSAGES = [
  'RoundOpened', 'PlayerDecided', 'RoundResolved', 'Snapshot', 'DraftOffer', 'PactOffer', 'PathOffer', 'InterludeOffer', 'InterludeResolved', 'LastWord', 'SignalReceived',
  'PlayerConnectionChanged', 'MatchFinished', 'ServerDraining', 'Error',
];

/** Razones de cierre del contrato (§2) tras las que el cliente no debe reconectar. */
export const NO_RECONNECT = new Set(['MATCH_FINISHED', 'SESSION_REPLACED', 'KICKED_BY_ADMIN', 'UNSUPPORTED_PROTOCOL_VERSION']);
const CLOSE_REASONS = [
  'TICKET_INVALID', 'UNSUPPORTED_PROTOCOL_VERSION', 'CONNECTION_LIMIT', 'MATCH_FINISHED', 'SERVER_DRAINING',
  'MESSAGE_TOO_LARGE', 'INTERNAL_ERROR', 'SESSION_REPLACED', 'KICKED_BY_ADMIN', 'RATE_LIMITED', 'HEARTBEAT_TIMEOUT',
];

/** Extrae `CODIGO` de "…CODIGO: descripción" (Close.error de SignalR). */
export function parseCloseReason(error) {
  if (!error) return null;
  const text = String(error.message ?? error);
  if (/Server timeout elapsed/i.test(text)) return 'HEARTBEAT_TIMEOUT';
  return CLOSE_REASONS.find((code) => text.includes(`${code}:`) || text.includes(code)) ?? null;
}

/**
 * Normaliza las claves a camelCase por si el serializador del servidor las deja en PascalCase.
 * El contrato exige camelCase; esto solo hace al cliente de prueba más tolerante.
 */
export function camelize(value) {
  if (Array.isArray(value)) return value.map(camelize);
  if (value instanceof Date) return value.toISOString();
  if (value && typeof value === 'object') {
    const out = {};
    for (const [k, v] of Object.entries(value)) out[k.charAt(0).toLowerCase() + k.slice(1)] = camelize(v);
    return out;
  }
  return value;
}

export class RealTransport {
  /** @param {string} matchId  @param {{json?: boolean}} [options] */
  constructor(matchId, options = {}) {
    this.matchId = matchId;
    this.useJson = Boolean(options.json);
    this.handlers = new Map();
    this.closeHandler = () => {};
    this.connection = null;
  }

  on(type, handler) { this.handlers.set(type, handler); }
  onClose(handler) { this.closeHandler = handler; }

  get protocolName() { return this.useJson ? 'json' : 'messagepack'; }

  async connect() {
    const signalR = window.signalR;
    if (!signalR) throw new Error('No se cargó la librería de SignalR.');
    const builder = new signalR.HubConnectionBuilder()
      .withUrl(`${HUB_PATH}?protocolVersion=${PROTOCOL_VERSION}`, {
        skipNegotiation: true,
        transport: signalR.HttpTransportType.WebSockets,
        // Se llama en cada start(): siempre un ticket nuevo (son de un solo uso, 30 s).
        accessTokenFactory: async () => (await createTicket(this.matchId)).ticket,
      })
      .configureLogging(signalR.LogLevel.Warning);
    if (!this.useJson) builder.withHubProtocol(new signalR.protocols.msgpack.MessagePackHubProtocol());

    const connection = builder.build();
    connection.serverTimeoutInMilliseconds = 30000;
    connection.keepAliveIntervalInMilliseconds = 10000;
    for (const type of SERVER_MESSAGES) {
      connection.on(type, (payload) => this.handlers.get(type)?.(camelize(payload)));
    }
    connection.onclose((error) => {
      if (this.connection !== connection) return;
      this.connection = null;
      this.closeHandler({ code: parseCloseReason(error), message: error ? String(error.message ?? error) : '' });
    });
    this.connection = connection;
    try {
      await connection.start();
    } catch (err) {
      this.connection = null;
      throw err;
    }
  }

  /** Invoca un método del hub. Devuelve el ACK o `{ error }` (InvocationError) tal como responde el servidor. */
  async invoke(method, payload) {
    if (!this.connection) throw new Error('Sin conexión');
    return camelize(await this.connection.invoke(method, payload));
  }

  get connected() { return this.connection?.state === 'Connected'; }

  async stop() {
    const connection = this.connection;
    this.connection = null;
    if (connection) await connection.stop().catch(() => {});
  }
}
