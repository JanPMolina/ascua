// Utilidades de DOM. Regla del cliente: los datos del servidor solo entran al DOM como texto
// (textContent / createTextNode); nunca se usa innerHTML.
import { emit } from './bus.js';

export const $ = (id) => {
  const node = document.getElementById(id);
  if (!node) throw new Error(`Falta el elemento #${id}`);
  return node;
};

/** Crea un elemento con atributos seguros y hijos (texto o nodos). */
export function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (value === undefined || value === null || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key === 'dataset') Object.assign(node.dataset, value);
    else if (key.startsWith('on')) { if (typeof value === 'function') node.addEventListener(key.slice(2), value); }
    else node.setAttribute(key, value === true ? '' : String(value));
  }
  for (const child of children.flat()) {
    if (child === undefined || child === null || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

export function setText(id, value) {
  $(id).textContent = value === undefined || value === null ? '' : String(value);
}

const screens = ['auth', 'lobby', 'room', 'queue', 'match', 'finished'];

export function showScreen(name) {
  const changed = $(`screen-${name}`).hidden;
  for (const s of screens) $(`screen-${s}`).hidden = s !== name;
  document.body.dataset.screen = name;
  if (changed) window.scrollTo({ top: 0 });
  emit('ui:screen', { name });
}

let toastTimer = 0;
let toastLeave = 0;

/** Aviso breve arriba de la pantalla. kind: 'info' | 'ok' | 'bad'. */
export function toast(message, kind = 'info', ms = 4000) {
  const box = $('toast');
  box.textContent = message;
  box.className = kind === 'bad' || kind === 'ok' ? `toast ${kind}` : 'toast';
  // Reinicia la animación de entrada aunque ya hubiera un aviso visible.
  box.hidden = true;
  void box.offsetWidth;
  box.hidden = false;
  clearTimeout(toastTimer);
  clearTimeout(toastLeave);
  toastTimer = setTimeout(() => {
    box.classList.add('leaving');
    toastLeave = setTimeout(() => { box.hidden = true; }, 250);
  }, ms);
}

export function formData(form) {
  return Object.fromEntries(new FormData(form).entries());
}

/** Deshabilita los botones del formulario mientras corre la promesa. */
export async function busy(form, fn) {
  const buttons = form.querySelectorAll('button');
  buttons.forEach((b) => { b.disabled = true; });
  form.setAttribute('aria-busy', 'true');
  try {
    return await fn();
  } finally {
    buttons.forEach((b) => { b.disabled = false; });
    form.removeAttribute('aria-busy');
  }
}
