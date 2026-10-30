// Utilidades de DOM. Regla del cliente: los datos del servidor solo entran al DOM como texto
// (textContent / createTextNode); nunca se usa innerHTML.

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
  for (const s of screens) $(`screen-${s}`).hidden = s !== name;
}

let toastTimer = 0;

/** Aviso breve arriba de la pantalla. */
export function toast(message, kind = 'info', ms = 4000) {
  const box = $('toast');
  box.textContent = message;
  box.className = kind === 'bad' ? 'toast bad' : 'toast';
  box.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { box.hidden = true; }, ms);
}

export function formData(form) {
  return Object.fromEntries(new FormData(form).entries());
}

/** Deshabilita los botones del formulario mientras corre la promesa. */
export async function busy(form, fn) {
  const buttons = form.querySelectorAll('button');
  buttons.forEach((b) => { b.disabled = true; });
  try {
    return await fn();
  } finally {
    buttons.forEach((b) => { b.disabled = false; });
  }
}
