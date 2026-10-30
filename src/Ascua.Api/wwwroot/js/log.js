// Panel de depuración: registra cada mensaje del hub (entrada, salida y eventos de conexión).
import { $, el } from './ui.js';

const MAX_ENTRIES = 500;
const entries = [];

/**
 * @param {'in'|'out'|'sys'} dir  in = servidor→cliente, out = cliente→servidor, sys = conexión/cliente.
 * @param {string} type           Nombre del mensaje del contrato (RoundOpened, SubmitOrder…).
 * @param {object} [payload]
 */
export function logMessage(dir, type, payload) {
  const entry = {
    at: new Date().toISOString(),
    dir,
    type,
    seq: pickSeq(payload),
    payload: payload ?? null,
  };
  entries.push(entry);
  if (entries.length > MAX_ENTRIES) entries.shift();
  render(entry);
}

function pickSeq(payload) {
  if (!payload || typeof payload !== 'object') return '';
  if (payload.seq !== undefined) return payload.previousSeq !== undefined ? `${payload.previousSeq}→${payload.seq}` : payload.seq;
  if (payload.snapshot?.seq !== undefined) return payload.snapshot.seq;
  return '';
}

function summarize(entry) {
  const p = entry.payload;
  if (!p || typeof p !== 'object') return p === null ? '' : String(p);
  if (p.error) return `${p.error.code}: ${p.error.message ?? ''}`;
  const parts = [];
  if (p.round !== undefined) parts.push(`ronda ${p.round}`);
  if (p.order) parts.push(p.order.direction ? `${p.order.type} ${p.order.direction}` : p.order.type);
  if (p.result) parts.push(p.result);
  if (p.clientActionId) parts.push(`id ${p.clientActionId.slice(-12)}`);
  if (p.decidedCount !== undefined) parts.push(`${p.decidedCount}/${p.expectedPlayers}`);
  if (p.outcome) parts.push(p.outcome);
  if (p.state) parts.push(p.state);
  if (p.options) parts.push(p.options.join(' / '));
  if (p.blessingId) parts.push(p.blessingId);
  if (p.message) parts.push(p.message);
  return parts.join(' · ');
}

function render(entry) {
  const body = $('log-body');
  const showPayload = $('log-payloads').checked;
  const detail = showPayload && entry.payload ? JSON.stringify(entry.payload) : summarize(entry);
  const arrow = { in: '←', out: '→', sys: '•' }[entry.dir];
  body.append(el('tr', { class: entry.dir },
    el('td', {}, entry.at.slice(11, 23)),
    el('td', {}, arrow),
    el('td', {}, String(entry.seq)),
    el('td', {}, entry.type),
    el('td', {}, detail)));
  while (body.childElementCount > MAX_ENTRIES) body.firstElementChild.remove();
  $('log-count').textContent = String(entries.length);
  body.scrollTop = body.scrollHeight;
}

export function setLastSeq(seq) {
  $('log-seq').textContent = seq === null || seq === undefined ? '' : `último seq aplicado: ${seq}`;
}

export function initLogPanel() {
  $('btn-log-clear').addEventListener('click', () => {
    entries.length = 0;
    $('log-body').replaceChildren();
    $('log-count').textContent = '0';
  });
  $('btn-log-copy').addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(JSON.stringify(entries, null, 2));
    } catch {
      // Sin permiso de portapapeles: no es crítico.
    }
  });
  $('log-payloads').addEventListener('change', () => {
    $('log-body').replaceChildren();
    const copy = entries.splice(0);
    copy.forEach((e) => { entries.push(e); render(e); });
  });
}
