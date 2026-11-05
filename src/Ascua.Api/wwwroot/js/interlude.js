// El interludio (planteamiento/06-version-3.md §3 y §4): El Juicio, las puertas y el recuento.
// Todo sale de `view.interlude` y de `InterludeOffer`, que solo traen el voto propio: de los demás se sabe quién votó, no qué.
// Sobrevive a recargar y reconectar porque la vista manda: se abre, se actualiza o se cierra según lo que llega.
import { $, el, setText } from './ui.js';
import { emit } from './bus.js';
import { heroAvatar } from './heroes.js';
import { ERROR_CODES, ROOM_TYPES, label } from './labels.js';

/** Pausa antes de tapar el tablero: se deja ver lo que acaba de pasar en la ronda. */
const LIVE_OPEN_DELAY_MS = 1800;
const VERDICT_MS = 9000;

const roomText = (type) => ROOM_TYPES[type] ?? { name: type, effect: '', icon: '▫' };

export class InterludeUi {
  /** @param {object} match  MatchController: invoke, nameOf, heroOf, playerId, matchId y clockOffset. */
  constructor(match) {
    this.match = match;
    this.offer = null;
    this.accused = null;
    this.door = null;
    this.sending = false;
    this.openTimer = 0;
    this.verdictTimer = 0;
    this.shownVerdict = null;
    $('btn-vote').onclick = () => this.vote();
  }

  dispose() {
    clearTimeout(this.openTimer);
    clearTimeout(this.verdictTimer);
    $('interlude').hidden = true;
    $('verdict').hidden = true;
  }

  get isOpen() {
    return !$('interlude').hidden;
  }

  /** Oferta nueva o actualizada. `live`: llega justo tras la ronda, así que espera un momento antes de abrirse. */
  sync(offer, { live = false } = {}) {
    if (!offer) {
      this.close();
      return;
    }
    const fresh = this.offer?.offerId !== offer.offerId;
    this.offer = offer;
    this.deadlineLocal = Date.parse(offer.deadline) - (this.match.clockOffset ?? 0);
    if (fresh) {
      this.accused = offer.myVote?.accusedId ?? null;
      this.door = offer.myVote?.door ?? null;
      this.sending = false;
      emit('interlude:offer', { doors: offer.doors });
    }
    if (offer.myVote) {
      this.accused = offer.myVote.accusedId ?? null;
      this.door = offer.myVote.door ?? null;
    }
    this.render();
    if (!this.isOpen && !this.openTimer) {
      if (live) {
        this.openTimer = setTimeout(() => { this.openTimer = 0; this.show(); }, LIVE_OPEN_DELAY_MS);
      } else {
        this.show();
      }
    }
  }

  show() {
    if (!this.offer) return;
    $('interlude').hidden = false;
    $('interlude').querySelector('button:not(:disabled)')?.focus();
    this.tick();
  }

  close() {
    clearTimeout(this.openTimer);
    this.openTimer = 0;
    this.offer = null;
    $('interlude').hidden = true;
  }

  render() {
    const o = this.offer;
    if (!o) return;
    const voted = Boolean(o.myVote);
    const locked = voted || !o.canVote || this.sending;
    setText('interlude-floor', `Sala ${o.endedFloor} despejada`);
    setText('interlude-title', o.doors.length ? 'El Juicio y las puertas' : 'El Juicio antes del Celador');
    setText('interlude-lead', o.canVote
      ? 'No corre el contador de rondas. Puedes acusar a quien sospeches, o a nadie.'
      : 'En este juicio no votas: los Traidores y quienes están eligiendo camino miran.');

    const candidate = (id) => {
      const on = this.accused === id;
      const hero = this.match.heroOf(id);
      return el('button', {
        type: 'button', class: `trial-pick${on ? ' chosen' : ''}`, role: 'radio', 'aria-checked': String(on), disabled: locked,
        onclick: () => { this.accused = id; this.render(); },
      }, hero ? heroAvatar(hero, 'sm') : null, el('b', {}, this.match.nameOf(id)), el('small', {}, o.voted.includes(id) ? '✔ ya votó' : o.voters.includes(id) ? 'pensando' : 'no vota'));
    };
    const nobody = el('button', {
      type: 'button', class: `trial-pick nobody${this.accused === null ? ' chosen' : ''}`, role: 'radio', 'aria-checked': String(this.accused === null), disabled: locked,
      onclick: () => { this.accused = null; this.render(); },
    }, el('span', { class: 'trial-none', 'aria-hidden': 'true' }, '∅'), el('b', {}, 'A nadie'), el('small', {}, 'Sin riesgo'));
    $('trial-options').replaceChildren(nobody, ...o.candidates.map(candidate));

    $('door-section').hidden = !o.doors.length;
    $('door-options').replaceChildren(...o.doors.map((d, i) => {
      const room = roomText(d.type);
      const on = this.door === i;
      return el('button', {
        type: 'button', class: `door${on ? ' chosen' : ''}`, role: 'radio', 'aria-checked': String(on), disabled: locked, dataset: { door: String(i) },
        onclick: () => { this.door = i; this.render(); },
      }, el('span', { class: 'door-ico', 'aria-hidden': 'true' }, room.icon), el('b', {}, d.name ?? room.name), el('small', {}, d.effect ?? room.effect));
    }));

    const button = $('btn-vote');
    button.hidden = !o.canVote || voted;
    button.disabled = this.sending;
    button.textContent = this.sending ? 'Enviando…' : 'Votar';
    setText('interlude-voted', `${o.voted.length}/${o.voters.length} votaron${voted ? ' · tu voto quedó registrado' : ''}`);
    $('interlude-voted').className = voted ? 'badge ok' : 'badge';
  }

  tick() {
    if (!this.offer) return;
    const left = Math.max(0, this.deadlineLocal - Date.now());
    setText('interlude-timer', `${Math.ceil(left / 1000)} s`);
    $('interlude-timer').classList.toggle('low', left < 5000);
    const bar = $('interlude-timebar');
    bar.style.width = `${Math.min(100, (left / 20000) * 100)}%`;
    bar.classList.toggle('low', left < 5000);
  }

  async vote() {
    const o = this.offer;
    if (!o || o.myVote || this.sending) return;
    this.sending = true;
    this.render();
    const payload = { matchId: this.match.matchId, offerId: o.offerId };
    if (this.accused) payload.accusedId = this.accused;
    if (this.door !== null && o.doors.length) payload.door = this.door;
    let failed = null;
    try {
      const res = await this.match.invoke('CastInterludeVote', payload);
      if (res?.error) failed = label(ERROR_CODES, res.error.code);
      else if (this.offer === o) o.myVote = { accusedId: this.accused, door: this.door };
      if (!res?.error) emit('interlude:voted', {});
    } catch {
      failed = 'Sin conexión: vuelve a votar al reconectar.';
    }
    this.sending = false;
    if (failed) this.match.toast(`Tu voto no salió: ${failed}`);
    this.render();
  }

  // ───────────── recuento ─────────────

  /** Líneas del recuento. Nunca dice quién votó qué: el contrato no lo trae. */
  verdictLines(v) {
    const lines = [];
    for (const a of v.accusations ?? []) {
      const who = this.match.nameOf(a.accusedId);
      const votes = `${a.votes} ${a.votes === 1 ? 'voto' : 'votos'} de ${a.otherVoters}`;
      if (!a.convicted) {
        lines.push({ tone: '', text: `${who}: ${votes}. Sin mayoría, no pasa nada.` });
      } else if (a.guilty) {
        lines.push({ tone: 'bad', text: `${who} es culpable (${votes}): pierde ${a.goldLost} de oro.` });
        for (const b of a.exposed ?? []) lines.push({ tone: 'bad', sub: true, text: `Le costó ${b.damage} de vida a ${this.match.nameOf(b.victimId)}.` });
      } else {
        lines.push({ tone: 'good', text: `${who} era inocente (${votes}): cada acusador pierde ${a.accuserFine} de oro.` });
      }
    }
    if (!lines.length) lines.push({ tone: '', text: 'Nadie acusó a nadie.' });
    if (v.noAccusation) lines.push({ tone: 'muted', text: `${v.noAccusation} ${v.noAccusation === 1 ? 'votó' : 'votaron'} «a nadie».` });
    if (v.chosenDoor) {
      const room = roomText(v.chosenDoor);
      const doors = (v.doors ?? []).map((d) => `${roomText(d.type).name} ${d.votes}`).join(' · ');
      lines.push({ tone: 'door', text: `Puerta: ${room.icon} ${room.name} (${doors}).` });
    }
    return lines;
  }

  /** Lista de líneas para pintar. */
  verdictList(v) {
    return el('ul', { class: 'verdict-lines' }, ...this.verdictLines(v).map((l) => el('li', { class: [l.tone, l.sub ? 'sub' : ''].filter(Boolean).join(' ') }, l.text)));
  }

  /** Tarjeta sobre el tablero: no tapa el reloj ni frena la ronda siguiente. */
  showVerdict(v) {
    const box = $('verdict');
    const guilty = (v.accusations ?? []).some((a) => a.convicted && a.guilty);
    box.className = `verdict-card${guilty ? ' guilty' : ''}`;
    const close = el('button', { type: 'button', class: 'ghost small verdict-close', 'aria-label': 'Cerrar el recuento', onclick: () => { box.hidden = true; } }, '✕');
    box.replaceChildren(el('b', { class: 'verdict-title' }, `El Juicio de la sala ${v.endedFloor}`), close, this.verdictList(v));
    box.hidden = false;
    clearTimeout(this.verdictTimer);
    this.verdictTimer = setTimeout(() => { box.hidden = true; }, VERDICT_MS);
    emit('interlude:verdict', { guilty, convicted: (v.accusations ?? []).some((a) => a.convicted), door: v.chosenDoor });
  }

  /** El último recuento queda a mano en la columna derecha; vuelve con la vista al recargar. */
  renderLog(v) {
    const box = $('verdict-log');
    box.hidden = !v;
    const key = v ? JSON.stringify(v) : null;
    if (!v || this.shownVerdict === key) return;
    this.shownVerdict = key;
    $('verdict-log-body').replaceChildren(this.verdictList(v));
  }
}
