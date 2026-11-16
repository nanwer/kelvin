import { icon } from './icons.js';
import { views } from './views.js';
import * as safetyNotice from './safety-notice.js';

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

// ---- Notifications for temperature alerts
// The service records alert events; whichever Kelvin window is open turns new ones into
// notifications. In the desktop app that's a Windows notification, even while it sits in the tray.
export const notificationsSupported = () => Boolean(tauriInvoke) || 'Notification' in window;

export async function enableBrowserNotifications() {
  if (tauriInvoke || !('Notification' in window)) return true;
  return (await Notification.requestPermission()) === 'granted';
}

async function showNotification(title, body) {
  if (tauriInvoke) {
    await tauriInvoke('plugin:notification|notify', { options: { title, body } });
  } else if ('Notification' in window && Notification.permission === 'granted') {
    new Notification(title, { body });
  }
}

// ---- Safety override notice: a floating card on the right (timing rules in safety-notice.js).
// State lives in sessionStorage so a reload doesn't bring back a notice you dismissed.
let safety = (() => {
  try { return { ...safetyNotice.initial(), ...JSON.parse(sessionStorage.getItem('kelvin.safety') || '{}') }; }
  catch { return safetyNotice.initial(); }
})();
let safetyReason = '';

function saveSafety() {
  try { sessionStorage.setItem('kelvin.safety', JSON.stringify(safety)); } catch { /* storage off: fine */ }
}

function updateSafetyNotice(s) {
  const on = Boolean(s.safety?.override);
  if (on && s.safety.reason) safetyReason = s.safety.reason;
  const { state: next, show } = safetyNotice.step(safety, on, Date.now());
  if (JSON.stringify(next) !== JSON.stringify(safety)) { safety = next; saveSafety(); }
  const el = document.getElementById('safety-notice');

  if (!show) {
    if (!el.hidden) { el.hidden = true; el.innerHTML = ''; el.dataset.key = ''; }
    return;
  }
  const title = show === 'override' ? 'Safety override' : 'Cooling down';
  const body = show === 'override' ? safetyReason : 'Temperatures are back under your safety limits, so fans are following their curves again. This closes after a minute without another override.';
  const key = `${title}|${body}`;
  if (el.dataset.key === key && !el.hidden) return;
  el.dataset.key = key;
  el.className = `float-notice ${show === 'override' ? 'crit' : 'calm'}`;
  el.innerHTML = `${icon('shield', 20)}
    <div class="grow"><strong>${title}</strong><p>${body.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c])}</p>
      <a href="#/settings">Safety limits</a></div>
    <button type="button" class="close" aria-label="Dismiss">${icon('close', 16)}</button>`;
  el.hidden = false;
}

document.getElementById('safety-notice').addEventListener('click', (e) => {
  if (!e.target.closest('.close')) return;
  safety = safetyNotice.dismiss(safety);
  saveSafety();
  const el = document.getElementById('safety-notice');
  el.hidden = true;
  el.innerHTML = '';
});

let lastAlertId = null;
function handleAlerts(s) {
  const recent = s.alerts?.recent ?? [];
  const newest = recent.reduce((m, e) => Math.max(m, e.id), 0);
  // On first connect, don't replay alerts from before this window opened.
  if (lastAlertId === null) { lastAlertId = newest; return; }
  const fresh = recent.filter((e) => e.id > lastAlertId).sort((a, b) => a.id - b.id).slice(-3);
  lastAlertId = Math.max(lastAlertId, newest);
  for (const e of fresh) showNotification(e.title, e.body).catch(() => {});
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
  handleAlerts(state);
  updateSafetyNotice(state);
  if (first) { route(); }
  else current?.view.update(state);
  const navKey = `${parseHash().id}|${state.recommendations.length}`;
  if (navKey !== lastNavKey) { renderNav(); lastNavKey = navKey; }
  renderStatus();
}

// Reconnect quickly at first, then back off (up to 15 s) while the service stays down.
let retryMs = 1000;
function connect() {
  const ws = new WebSocket(LIVE_URL);
  ws.onopen = () => { retryMs = 1000; };
  ws.onmessage = (e) => onState(JSON.parse(e.data));
  ws.onclose = () => {
    connected = false;
    renderStatus();
    setTimeout(connect, retryMs);
    retryMs = Math.min(retryMs * 1.6, 15000);
  };
}

window.addEventListener('hashchange', route);
api('GET', '/state').then(onState).catch(() => renderStatus());
connect();
