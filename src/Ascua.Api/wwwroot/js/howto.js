// Guía «Cómo jugar»: páginas estáticas de index.html (#howto) con anterior/siguiente.
// Se abre desde la barra superior y desde la pantalla de inicio; no depende del servidor.
import { $, setText } from './ui.js';

let page = 0;

const pages = () => [...document.querySelectorAll('#howto .howto-page')];

function show(index) {
  const all = pages();
  page = Math.max(0, Math.min(all.length - 1, index));
  all.forEach((p, i) => { p.hidden = i !== page; });
  setText('howto-count', `${page + 1} / ${all.length}`);
  $('howto-prev').disabled = page === 0;
  $('howto-next').hidden = page === all.length - 1;
}

export function openHowTo() {
  show(0);
  $('howto').hidden = false;
  $('howto-next').focus();
}

function closeHowTo() {
  $('howto').hidden = true;
}

export function initHowTo() {
  $('btn-howto').addEventListener('click', openHowTo);
  $('btn-howto-auth').addEventListener('click', openHowTo);
  $('howto-close').addEventListener('click', closeHowTo);
  $('howto-prev').addEventListener('click', () => show(page - 1));
  $('howto-next').addEventListener('click', () => show(page + 1));
  // Clic fuera del cuadro: cerrar.
  $('howto').addEventListener('click', (e) => { if (e.target === e.currentTarget) closeHowTo(); });
  document.addEventListener('keydown', (e) => {
    if ($('howto').hidden) return;
    if (e.key === 'Escape') closeHowTo();
    else if (e.key === 'ArrowRight') show(page + 1);
    else if (e.key === 'ArrowLeft') show(page - 1);
  });
}
