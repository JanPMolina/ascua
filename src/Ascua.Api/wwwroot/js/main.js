// Punto de entrada. ?demo=1 → partida simulada sin servidor; ?tutorial=1 → práctica guiada sin servidor;
// ?json=1 → hub con JSON (solo local/dev).
import * as api from './api.js';
import { $, busy, formData, setText, showScreen, toast } from './ui.js';
import { initLogPanel } from './log.js';
import { initLobby, showLobby, stopLobby } from './lobby.js';
import { startMatch, stopMatch } from './match.js';
import { RealTransport } from './hub.js';
import { DemoTransport } from './demo.js';
import { startTutorial } from './tutorial.js';
import { initHowTo } from './howto.js';
import { initSettingsPanel } from './settings-panel.js';

const params = new URLSearchParams(location.search);
const DEMO = params.get('demo') === '1';
const TUTORIAL = params.get('tutorial') === '1';
const USE_JSON = params.get('json') === '1';

function enterMatch(matchId) {
  startMatch({ transport: new RealTransport(matchId, { json: USE_JSON }), onExit: showLobby });
}

function startDemo() {
  startMatch({ transport: new DemoTransport(), onExit: startDemo });
}

function bindAuth() {
  $('form-login').addEventListener('submit', (e) => {
    e.preventDefault();
    const form = e.currentTarget;
    const { username, password } = formData(form);
    busy(form, async () => {
      try {
        await api.login(username.trim(), password);
        form.reset();
      } catch (err) {
        toast(`No se pudo iniciar sesión: ${err.message}`, 'bad');
      }
    });
  });

  $('form-register').addEventListener('submit', (e) => {
    e.preventDefault();
    const form = e.currentTarget;
    if (!form.checkValidity()) {
      form.reportValidity();
      return;
    }
    const { username, email, password } = formData(form);
    busy(form, async () => {
      try {
        await api.register(username.trim(), email.trim(), password);
        await api.login(username.trim(), password);
        form.reset();
        toast('Cuenta creada. ¡Bienvenido al Umbral!', 'ok');
      } catch (err) {
        toast(`No se pudo registrar: ${err.message}`, 'bad', 7000);
      }
    });
  });

  $('btn-logout').addEventListener('click', () => api.logout());

  api.onSessionChange((loggedIn) => {
    $('btn-logout').hidden = !loggedIn;
    setText('user-label', loggedIn ? api.currentUsername() : '');
    if (loggedIn) {
      showLobby();
    } else {
      stopMatch();
      stopLobby();
      showScreen('auth');
      toast('Sesión cerrada.');
    }
  });
}

function main() {
  initLogPanel();
  initSettingsPanel();
  initHowTo();
  if (TUTORIAL) {
    $('demo-badge').textContent = 'TUTORIAL (sin servidor)';
    $('demo-badge').hidden = false;
    startTutorial(startMatch);
    return;
  }
  if (DEMO) {
    $('demo-badge').hidden = false;
    startDemo();
    return;
  }
  bindAuth();
  initLobby({ onEnterMatch: enterMatch });
  showScreen('auth');
}

main();
