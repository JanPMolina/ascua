// Panel de ajustes (botón ⚙ de la barra superior): silencio, volúmenes y «reducir movimiento».
// Solo llama a updateSettings: el audio escucha settings:changed por el bus. «Reducir movimiento» además
// pone la clase reduce-motion en <html>, que apaga las animaciones CSS (ver css/app.css).
import { $ } from './ui.js';
import { on } from './bus.js';
import { getSettings, updateSettings } from './settings.js';

let opener = null;

function applyMotion({ reducedMotion }) {
  document.documentElement.classList.toggle('reduce-motion', Boolean(reducedMotion));
}

function paintRange(input, out) {
  input.style.setProperty('--fill', `${input.value}%`);
  out.textContent = `${input.value}%`;
}

function render(s) {
  $('set-muted').checked = s.muted;
  $('set-motion').checked = s.reducedMotion;
  for (const [id, key] of [['set-sfx', 'sfxVolume'], ['set-music', 'musicVolume']]) {
    const input = $(id);
    if (document.activeElement !== input) input.value = String(Math.round(s[key] * 100));
    paintRange(input, $(`${id}-out`));
    input.disabled = s.muted;
  }
}

/** Sonido de prueba al soltar el deslizador de efectos, si el módulo de audio lo ofrece. */
async function preview() {
  try {
    const audio = await import('./audio.js');
    audio.previewSfx?.();
  } catch {
    // Sin módulo de audio: no hay vista previa.
  }
}

function open() {
  opener = document.activeElement;
  render(getSettings());
  $('settings').hidden = false;
  $('set-muted').focus();
}

function close() {
  $('settings').hidden = true;
  opener?.focus?.();
}

export function initSettingsPanel() {
  applyMotion(getSettings());
  on('settings:changed', (s) => { applyMotion(s); if (!$('settings').hidden) render(s); });

  $('btn-settings').addEventListener('click', open);
  $('settings-close').addEventListener('click', close);
  $('settings').addEventListener('click', (e) => { if (e.target === e.currentTarget) close(); });
  $('set-muted').addEventListener('change', (e) => updateSettings({ muted: e.target.checked }));
  $('set-motion').addEventListener('change', (e) => updateSettings({ reducedMotion: e.target.checked }));
  for (const [id, key] of [['set-sfx', 'sfxVolume'], ['set-music', 'musicVolume']]) {
    const input = $(id);
    input.addEventListener('input', () => {
      paintRange(input, $(`${id}-out`));
      updateSettings({ [key]: Number(input.value) / 100 });
    });
    if (key === 'sfxVolume') input.addEventListener('change', preview);
  }

  document.addEventListener('keydown', (e) => {
    if ($('settings').hidden) return;
    if (e.key === 'Escape') {
      e.stopPropagation();
      close();
      return;
    }
    // Foco atrapado dentro del diálogo.
    if (e.key === 'Tab') {
      const focusable = [...$('settings').querySelectorAll('button, input')].filter((n) => !n.disabled);
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    }
  }, true);
}

/** ¿Hay un diálogo de la interfaz abierto? (la partida no debe leer el teclado mientras tanto). */
export const settingsOpen = () => !$('settings').hidden;
