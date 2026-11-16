import { icon } from './icons.js';
import { views } from './views.js';

const ROUTES = [
  { id: 'overview', label: 'Overview' },
  { id: 'fans', label: 'Fans' },
  { id: 'lighting', label: 'Lighting' },
  { id: 'health', label: 'Health' },
  { id: 'advice', label: 'Advice' },
  { id: 'settings', label: 'Settings' },
];

const main = document.getElementById('main');
const nav = document.getElementById('nav');
const railStatus = document.getElementById('rail-status');
let state = null;
let current = null; // { id, view }
let connected = false;
const startedAt = Date.now();

// In a browser the service serves this page itself. In the desktop app the page comes from the
// app bundle, so requests go to the service's fixed local address.
const SERVICE = /^(127\.0\.0\.1|localhost)$/.test(location.hostname) ? '' : 'http://127.0.0.1:5577';
const LIVE_URL = SERVICE ? 'ws://127.0.0.1:5577/api/live' : `ws://${location.host}/api/live`;

export async function api(method, path, body) {
  const res = await fetch(`${SERVICE}/api${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', 'X-Kelvin': '1' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    let msg = `Kelvin couldn't do that (error ${res.status}).`;
    try { msg = (await res.json()).error ?? msg; } catch { /* no body */ }
    throw new Error(msg);
  }
  const text = await res.text();
  return text ? JSON.parse(text) : null;
}

let toastTimer;
export function toast(message, isError = false) {
  const el = document.getElementById('toast');
  el.textContent = message;
  el.classList.toggle('error', isError);
  el.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove('show'), isError ? 5000 : 2400);
}

export const getState = () => state;

// ---- Updates (desktop app only: the Tauri shell checks GitHub Releases and verifies signatures)
const tauriInvoke = window.__TAURI__?.core?.invoke;
let update = null; // { version, currentVersion, notes } when a newer Kelvin exists
export const getUpdate = () => update;
export const canUpdate = () => Boolean(tauriInvoke);

export async function checkForUpdate(manual = false) {
  if (!tauriInvoke) return null;
  try {
    update = await tauriInvoke('check_for_update');
    if (manual && !update) toast('You have the latest version of Kelvin');
  } catch (e) {
    // Automatic checks fail quietly (offline, GitHub down); a manual check says why.
    if (manual) toast(`Couldn't check for updates: ${e}`, true);
  }
  if (state) current?.view.update(state);
  return update;
}

export async function installUpdate() {
  if (!tauriInvoke) return;
  toast(`Downloading Kelvin ${update?.version ?? ''}…`);
  try { await tauriInvoke('install_update'); } // the app restarts when this succeeds
  catch (e) { toast(String(e), true); }
}

if (tauriInvoke) {
  setTimeout(() => checkForUpdate(), 10_000);
  setInterval(() => checkForUpdate(), 6 * 60 * 60 * 1000);
}

function parseHash() {
  const [, id = 'overview', ...rest] = location.hash.split('/');
  return { id: ROUTES.some((r) => r.id === id) ? id : 'overview', param: rest.length ? decodeURIComponent(rest.join('/')) : null };
}

export function go(id, param) {
  location.hash = `#/${id}${param ? `/${encodeURIComponent(param)}` : ''}`;
}

function route() {
  if (!state) return;
  const { id, param } = parseHash();
  if (current?.id === id && current.view.setParam) {
    current.view.setParam(param);
  } else {
    current?.view.destroy?.();
    main.innerHTML = '';
    current = { id, view: views[id](main, { param }) };
    main.scrollTop = 0;
  }
  current.view.update(state);
  renderNav();
}

function renderNav() {
  const { id } = parseHash();
  const count = state?.recommendations.filter((r) => r.severity !== 'info').length ?? 0;
  nav.innerHTML = ROUTES.map((r) => `
    <li><a href="#/${r.id}" ${r.id === id ? 'aria-current="page"' : ''}>
      ${icon(r.id)}<span class="label">${r.label}</span>
      ${r.id === 'advice' && count ? `<span class="count" aria-label="${count} suggestions">${count}</span>` : ''}
    </a></li>`).join('');
}

function renderStatus() {
  if (!connected) {
    railStatus.innerHTML = '<div class="row"><span class="dot crit"></span>Service not running</div>';
    if (!state) {
      const waited = (Date.now() - startedAt) / 1000;
      main.innerHTML = waited < 20
        ? '<div class="connecting">Starting the Kelvin service…</div>'
        : `<div class="connecting"><p><strong>Kelvin's background service isn't responding.</strong></p>
           <p class="muted" style="margin-top:8px;max-width:60ch">Open Services in Windows, find "Kelvin hardware service" and start it, or reinstall Kelvin. This window connects as soon as it's running.</p></div>`;
    }
    return;
  }
  const s = state.system;
  const rows = [];
  if (state.safety.override) rows.push('<div class="row"><span class="dot crit"></span>Safety override on</div>');
  rows.push(s.simulated && !s.demo
    ? '<div class="row"><span class="dot warn"></span>Simulated hardware</div>'
    : `<div class="row"><span class="dot ok"></span>Fans via ${s.fanBackend}</div>`);
  const rgbWaiting = s.openRgb === 'starting' || s.openRgb === 'runningManaged';
  rows.push(s.rgbConnected
    ? `<div class="row"><span class="dot ok"></span>Lighting via ${s.rgbBackend}</div>`
    : rgbWaiting
      ? '<div class="row"><span class="dot warn"></span>Lighting starting…</div>'
      : `<div class="row"><span class="dot warn"></span>${s.openRgb === 'notInstalled' ? 'OpenRGB not installed' : 'Lighting not connected'}</div>`);
  railStatus.innerHTML = rows.join('');
}

let lastNavKey = '';
function onState(next) {
  const first = !state;
  state = next;
  connected = true;
  if (first) { route(); }
  else current?.view.update(state);
  const navKey = `${parseHash().id}|${state.recommendations.length}`;
  if (navKey !== lastNavKey) { renderNav(); lastNavKey = navKey; }
  renderStatus();
}

function connect() {
  const ws = new WebSocket(LIVE_URL);
  ws.onmessage = (e) => onState(JSON.parse(e.data));
  ws.onclose = () => {
    connected = false;
    renderStatus();
    setTimeout(connect, 1500);
  };
}

window.addEventListener('hashchange', route);
api('GET', '/state').then(onState).catch(() => renderStatus());
connect();
