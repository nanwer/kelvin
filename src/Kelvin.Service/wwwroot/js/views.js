import {
  api, toast, go, getUpdate, canUpdate, checkForUpdate, installUpdate,
  canUpdate as isDesktopApp, notificationsSupported, enableBrowserNotifications,
} from './app.js';
import { icon } from './icons.js';
import { createCurveEditor } from './curve.js';
import {
  thermal, esc, fmtTemp, fmtRpm, fmtPct, clone, same, debounce,
  temperatureSensors, sensorLabel, describeControl,
} from './util.js';

const ROLE_ORDER = { cpu: 0, gpu: 1, board: 2 };
const ROLE_NAME = { cpu: 'CPU', gpu: 'Graphics card', board: 'Motherboard' };

// ---------------------------------------------------------------- shared pieces

function banners(state, { showSimulated = false } = {}) {
  const out = [];
  const update = getUpdate();
  if (update) {
    out.push(`<div class="banner info">${icon('advice')}<div class="grow"><strong>Kelvin ${esc(update.version)} is available</strong>
      <p>You have ${esc(update.currentVersion)}. Updating takes under a minute. Your fans go back to BIOS control for those few seconds, then Kelvin picks up where it left off.</p></div>
      <button class="btn sm primary" data-install-update>Update now</button></div>`);
  }
  // The safety override has its own floating notice (see safetyNotice in app.js).
  for (const id of state.alerts?.firing ?? []) {
    const rule = state.alertSettings?.rules.find((r) => r.id === id);
    if (!rule) continue;
    const now = alertSensor(state, rule.source)?.value;
    out.push(`<div class="banner warn" role="status">${icon('alert')}<div class="grow"><strong>${esc(SOURCE_NAME[rule.source])} is above your ${Math.round(rule.threshold)}°C alert</strong>
      <p>${now != null ? `Now ${Math.round(now)}°C. ` : ''}Kelvin notified you when it crossed the limit. This clears once it's ${Math.round(rule.threshold - 3)}°C or cooler.</p></div>
      <a class="btn sm" href="#/settings">Alerts</a></div>`);
  }
  if (state.system.fanDriverInstalled === false) {
    out.push(`<div class="banner warn">${icon('alert')}<div class="grow"><strong>Motherboard fans need the PawnIO driver</strong>
      <p>Kelvin reaches motherboard fans and RAM lighting through PawnIO, a small signed driver. Kelvin's installer sets it up, but it couldn't this time (often because the PC was offline). Run the Kelvin installer again, or install it from pawnio.eu, then restart your PC.</p></div></div>`);
  }
  if (state.conflicts.length) {
    const names = state.conflicts.map((c) => c.name);
    const list = names.length > 2 ? `${names.slice(0, -1).join(', ')} and ${names.at(-1)}` : names.join(' and ');
    const closable = state.conflicts.filter((c) => c.canClose);
    const manual = state.conflicts.filter((c) => !c.canClose);
    const manualNote = manual.length
      ? ` ${manual.map((c) => c.name).join(' and ')} has to be closed from its tray icon, so it can hand your fans back to the BIOS.`
      : '';
    out.push(`<div class="banner warn">${icon('alert')}<div class="grow"><strong>${esc(list)} ${names.length > 1 ? 'are' : 'is'} also controlling your hardware</strong>
      <p>Two apps writing to the same device makes lighting flicker and fan speeds jump.${esc(manualNote)}</p></div>
      <div class="actions">
        ${closable.length ? `<button class="btn sm primary" data-close-conflicts="${esc(closable.map((c) => c.key).join(','))}" data-names="${esc(closable.map((c) => c.name).join(', '))}">Close ${closable.length > 1 ? 'them' : esc(closable[0].name)} for me</button>` : ''}
        <a class="btn sm" href="#/advice">Details</a>
      </div></div>`);
  }
  if (showSimulated && state.system.simulated && !state.system.demo) {
    out.push(`<div class="banner warn">${icon('info')}<div class="grow"><strong>You're looking at simulated hardware</strong>
      <p>${state.system.elevated ? 'The service was started with --simulate.' : 'The Kelvin service needs administrator rights to read and control your real fans.'} Everything works, but nothing reaches your PC.</p></div></div>`);
  }
  return out.join('');
}

const SOURCE_NAME = { cpu: 'CPU', gpu: 'Graphics card', board: 'Motherboard', hottest: 'Hottest of CPU and GPU' };

// The temperature an alert rule watches, matching AlertMonitor.SensorFor on the server.
function alertSensor(state, source) {
  if (source === 'hottest') return state.sensors.find((s) => s.id === 'virtual/hottest');
  return state.sensors.find((s) => s.primary && s.kind === 'temperature' && s.role === source);
}

// What to say when lighting isn't connected, based on what Kelvin knows about OpenRGB.
function lightingStatus(state) {
  switch (state.system.openRgb) {
    case 'notInstalled':
      return { title: 'Lighting needs OpenRGB', detail: 'Kelvin normally includes OpenRGB, but its copy is missing. Reinstall Kelvin to restore it.' };
    case 'starting':
    case 'runningManaged':
      return { title: 'Finding your lighting devices…', detail: 'OpenRGB is scanning your hardware in the background. The first scan can take up to half a minute.', busy: true };
    case 'failed':
      return { title: 'OpenRGB didn\'t start', detail: 'Kelvin will try again shortly. If this keeps happening, reinstall OpenRGB from openrgb.org.' };
    case 'runningExternal':
      return { title: 'OpenRGB is open, but its SDK server is off', detail: 'In OpenRGB, open the SDK Server tab and click Start Server. Or close OpenRGB and Kelvin will run it for you in the background.' };
    default:
      return { title: 'Lighting isn\'t connected', detail: state.system.rgbStatus ?? '' };
  }
}

function historyChart(values) {
  if (!values?.length) return '';
  const w = 300, h = 44;
  const min = Math.min(...values), max = Math.max(...values);
  const lo = Math.min(min, (min + max) / 2 - 3), hi = Math.max(max, (min + max) / 2 + 3);
  const n = Math.max(values.length - 1, 1);
  const pts = values.map((v, i) => `${((i / n) * w).toFixed(1)},${(h - 3 - ((v - lo) / (hi - lo)) * (h - 6)).toFixed(1)}`);
  const color = thermal(values.at(-1));
  return `<svg viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" aria-hidden="true">
    <path d="M${pts.join(' L')} L${w},${h} L0,${h} Z" fill="${color}" fill-opacity="0.1"/>
    <path d="M${pts.join(' L')}" fill="none" stroke="${color}" stroke-width="1.75" vector-effect="non-scaling-stroke" stroke-linejoin="round"/>
  </svg><span class="range">${fmtTemp(min)}–${fmtTemp(max)}°</span>`;
}

function ledStrip(colors, count) {
  const n = Math.min(colors?.length || count || 1, 24);
  return `<div class="leds" aria-hidden="true">${Array.from({ length: n }, (_, i) => `<i style="background:${colors?.[i] ?? '#000'}"></i>`).join('')}</div>`;
}

function updateLeds(el, colors) {
  if (!el || !colors) return;
  const cells = el.children;
  for (let i = 0; i < cells.length; i++) cells[i].style.background = colors[i] ?? '#000';
}

const primaryTemps = (state) =>
  state.sensors
    .filter((s) => s.primary && s.kind === 'temperature')
    .sort((a, b) => (ROLE_ORDER[a.role] ?? 9) - (ROLE_ORDER[b.role] ?? 9));

async function run(action, ok) {
  try { await action(); if (ok) toast(ok); } catch (e) { toast(e.message, true); }
}

// Opens a link in the user's browser: through the desktop shell in the app, a new tab otherwise.
async function openExternal(url) {
  const invoke = window.__TAURI__?.core?.invoke;
  if (invoke) await invoke('plugin:opener|open_url', { url });
  else window.open(url, '_blank', 'noopener');
}

async function closeConflicts(keys, names) {
  const msg = `Close ${names}?\n\nKelvin will quit ${keys.length > 1 ? 'these apps and their background services' : 'it and its background service'} so they stop overriding your lighting. Anything saved to your devices' onboard memory keeps working.`;
  if (!confirm(msg)) return;
  const closed = [];
  for (const key of keys) {
    try {
      const r = await api('POST', '/conflicts/close', { id: key });
      closed.push(r.name);
    } catch (e) { toast(e.message, true); return; }
  }
  if (closed.length) toast(`Closed ${closed.join(' and ')}`);
}

// Banners are re-rendered on every update, so their buttons are handled here, once.
document.addEventListener('click', (e) => {
  const b = e.target.closest('[data-close-conflicts]');
  if (b) closeConflicts(b.dataset.closeConflicts.split(','), b.dataset.names);
  const u = e.target.closest('[data-install-update]');
  if (u) { u.disabled = true; u.textContent = 'Updating…'; installUpdate(); }
});

// ---------------------------------------------------------------- Overview

function overview(root) {
  root.innerHTML = `<div class="page">
    <header class="page-head"><div><h1>Overview</h1><p id="ov-sub"></p></div></header>
    <div class="stack" id="ov-banners"></div>
    <section class="panel">
      <div class="panel-head"><h2>Temperatures</h2><span class="meta">Last 2 minutes</span></div>
      <div class="thermals" id="ov-thermals"></div>
      <div class="ruler" id="ov-ruler" role="img" aria-label="Where each temperature sits between 35 and 85 degrees"></div>
      <div class="ruler-labels"><span>35°C cool</span><span>60°C</span><span>85°C hot</span></div>
    </section>
    <div class="grid-2">
      <section class="panel">
        <div class="panel-head"><h2>Fans</h2><a class="btn sm ghost" href="#/fans">Edit curves</a></div>
        <table><thead><tr><th>Fan</th><th class="num">Speed</th><th>Duty</th><th>Control</th></tr></thead><tbody id="ov-fans"></tbody></table>
      </section>
      <section class="panel">
        <div class="panel-head"><h2>Lighting</h2><a class="btn sm ghost" href="#/lighting" id="ov-effect">Change</a></div>
        <div id="ov-lights"></div>
      </section>
    </div>
  </div>`;

  const $ = (id) => root.querySelector(id);
  let keys = {};

  return {
    update(state) {
      const temps = primaryTemps(state);
      const fans = state.fans;
      const devices = state.devices;
      const advice = state.recommendations.filter((r) => r.severity !== 'info').length;
      $('#ov-sub').textContent = [
        `${fans.filter((f) => f.profile?.connected !== false).length} fans`,
        `${devices.length} lighting devices`,
        advice ? `${advice} suggestion${advice > 1 ? 's' : ''} waiting` : 'nothing needs attention',
      ].join(' · ');

      const b = banners(state, { showSimulated: true });
      if (b !== keys.banners) { $('#ov-banners').innerHTML = b; keys.banners = b; }

      const tKey = temps.map((t) => t.id).join();
      if (tKey !== keys.temps) {
        $('#ov-thermals').innerHTML = temps.map((t) => `
          <div class="thermal-row" data-id="${esc(t.id)}">
            <div class="label"><strong>${ROLE_NAME[t.role] ?? esc(t.name)}</strong><span>${esc(t.hardware)}</span></div>
            <div class="temp"></div>
            <div class="history"></div>
          </div>`).join('');
        $('#ov-ruler').innerHTML = temps.map((t) => `<span class="tick" data-id="${esc(t.id)}" title="${ROLE_NAME[t.role] ?? esc(t.name)}"></span>`).join('');
        keys.temps = tKey;
      }
      for (const t of temps) {
        const row = root.querySelector(`.thermal-row[data-id="${CSS.escape(t.id)}"]`);
        row.querySelector('.temp').innerHTML = `<span style="color:${thermal(t.value)}">${fmtTemp(t.value)}</span><small>°C</small>`;
        row.querySelector('.history').innerHTML = historyChart(state.history[t.id]);
        const tick = root.querySelector(`.tick[data-id="${CSS.escape(t.id)}"]`);
        tick.style.left = `${Math.min(100, Math.max(0, ((t.value - 35) / 50) * 100))}%`;
      }

      $('#ov-fans').innerHTML = fans.map((f) => {
        const empty = f.profile && !f.profile.connected;
        return `<tr class="link" data-id="${esc(f.id)}" tabindex="0">
          <td><strong>${esc(f.name)}</strong>${empty ? '<div class="faint">Nothing connected</div>' : ''}</td>
          <td class="num">${fmtRpm(f.rpm)} <span class="faint">rpm</span></td>
          <td><div class="duty"><div class="bar"><i style="width:${f.percent ?? 0}%"></i></div><span>${fmtPct(f.percent)}</span></div></td>
          <td><span class="tag ${f.settings && f.settings.mode !== 'bios' ? 'accent' : ''}">${esc(describeControl(f, state))}</span></td>
        </tr>`;
      }).join('');

      $('#ov-effect').textContent = state.system.rgbConnected ? `${cap(state.lighting.effect)} · Change` : 'Set up';
      const lKey = devices.map((d) => d.id + d.included).join() + state.system.rgbConnected + state.system.openRgb;
      if (lKey !== keys.lights) {
        $('#ov-lights').innerHTML = !state.system.rgbConnected
          ? (() => { const s = lightingStatus(state); return `<div class="empty">${icon(s.busy ? 'lighting' : 'plug', 22)}<strong>${esc(s.title)}</strong><span>${esc(s.detail)}</span></div>`; })()
          : devices.map((d) => `<div class="device ${d.included ? '' : 'off'}" data-id="${esc(d.id)}" style="grid-template-columns:1fr minmax(100px,160px)">
              <div><strong>${esc(d.name)}</strong><span class="faint">${esc(d.vendor)} · ${esc(d.kind)}${d.included ? '' : ' · left alone'}</span></div>
              ${ledStrip(state.preview[d.id], d.ledCount)}</div>`).join('');
        keys.lights = lKey;
      }
      for (const d of devices) updateLeds(root.querySelector(`.device[data-id="${CSS.escape(d.id)}"] .leds`), state.preview[d.id]);
    },
    destroy() {},
  };

  function cap(s) { return s[0].toUpperCase() + s.slice(1); }
}

function bindRowLinks(root) {
  root.addEventListener('click', (e) => {
    const tr = e.target.closest('tr.link');
    if (tr) go('fans', tr.dataset.id);
  });
  root.addEventListener('keydown', (e) => {
    const tr = e.target.closest('tr.link');
    if (tr && e.key === 'Enter') go('fans', tr.dataset.id);
  });
}

// ---------------------------------------------------------------- Fans

const DEFAULT_RESPONSE = 6;

function fansView(root, { param }) {
  root.innerHTML = `<div class="page">
    <header class="page-head"><div><h1>Fans</h1><p>Pick a fan, choose who controls it, and shape how fast it spins as things heat up.</p></div>
      <div class="actions"><button class="btn" id="fx-calibrate">Measure fans</button></div></header>
    <div class="stack" id="fx-banners"></div>
    <section class="panel laptop-modes" id="fx-laptop" hidden></section>
    <div class="fan-layout">
      <nav class="panel fan-list" id="fx-list" aria-label="Fans"></nav>
      <section class="panel" id="fx-editor"></section>
    </div>
  </div>`;

  let selected = param;
  let draft = null;
  let base = null;
  let curve = null;
  let lastState = null;
  let bannerKey = '';
  let profileKey = '';
  let listKey = '';

  // Laptop fan modes: whole-system modes for PCs whose fans can't be driven one by one.
  let laptopKey = '';
  function renderLaptop(state) {
    const el = root.querySelector('#fx-laptop');
    const l = state.laptop;
    el.hidden = !l;
    if (!l) return;
    const key = JSON.stringify(l);
    if (key === laptopKey) return;
    laptopKey = key;
    const anyControllable = state.fans.some((f) => f.controllable);
    const current = l.modes.find((m) => m.id === l.current);
    el.innerHTML = `
      <div class="panel-head"><h2>Laptop fan mode</h2><span class="meta">${esc(l.name)}${l.experimental ? ' · <span class="tag warn">Experimental</span>' : ''}</span></div>
      <div class="panel-body stack" style="gap:12px">
        <div class="segmented" role="group" aria-label="Laptop fan mode" id="fx-mode-set">
          ${l.modes.map((m) => `<button type="button" data-mode="${m.id}" aria-pressed="${m.id === l.current}" title="${esc(m.description)}">${esc(m.label)}</button>`).join('')}
        </div>
        <p class="muted">${current ? esc(current.description) : 'Pick a mode. It saves into your active profile.'}
          ${anyControllable ? '' : ' This laptop controls its own fans, so Kelvin sets the mode and the laptop does the rest.'}</p>
        ${l.experimental ? `<p class="faint">Nobody has confirmed this interface on your model yet. If a mode doesn't seem to change anything, or something looks wrong, please send a hardware report from Settings.</p>` : ''}
      </div>`;
  }
  root.querySelector('#fx-laptop').addEventListener('click', (e) => {
    const b = e.target.closest('[data-mode]'); if (!b) return;
    run(() => api('PUT', '/fan-mode', { id: b.dataset.mode }), `Fan mode: ${b.textContent}`);
  });

  root.querySelector('#fx-calibrate').addEventListener('click', () =>
    run(() => api('POST', '/fans/calibrate'), 'Measuring fans. This takes about 30 seconds.'));

  root.querySelector('#fx-list').addEventListener('click', (e) => {
    const b = e.target.closest('.fan-item');
    if (!b) return;
    if (draft && !same(draft, base) && !confirm('Discard your unsaved changes to this fan?')) return;
    go('fans', b.dataset.id);
  });

  function settingsFor(fan) {
    if (fan.settings) return clone(fan.settings);
    // A GPU fan follows the GPU; everything else starts out following the CPU.
    const role = /gpu/i.test(fan.name) ? 'gpu' : 'cpu';
    const src = lastState.sensors.find((s) => s.primary && s.role === role);
    return {
      fanId: fan.id, mode: 'bios', fixedPercent: 50, sourceSensorId: src?.id ?? 'virtual/hottest',
      points: clone(lastState.presets.balanced), minPercent: 20, responseSeconds: DEFAULT_RESPONSE, preset: 'balanced',
    };
  }

  function renderList(state) {
    const sub = (f) => (f.profile && !f.profile.connected ? 'Nothing connected' : describeControl(f, state));
    const key = JSON.stringify([selected, state.fans.map((f) => [f.id, f.name, sub(f)])]);
    const list = root.querySelector('#fx-list');
    // Rebuild only when something structural changes, so keyboard focus survives live updates.
    if (key !== listKey) {
      listKey = key;
      list.innerHTML = state.fans.map((f) => `
        <button class="fan-item" data-id="${esc(f.id)}" aria-current="${f.id === selected}">
          <strong>${esc(f.name)}</strong><span class="rpm"></span>
          <span class="sub">${esc(sub(f))}</span>
        </button>`).join('');
    }
    for (const f of state.fans) {
      const el = list.querySelector(`.fan-item[data-id="${CSS.escape(f.id)}"] .rpm`);
      if (el) el.innerHTML = `${fmtRpm(f.rpm)} <span class="faint">rpm</span>`;
    }
  }

  function renderEditor() {
    const state = lastState;
    const fan = state.fans.find((f) => f.id === selected);
    const el = root.querySelector('#fx-editor');
    if (!fan) { el.innerHTML = '<div class="empty"><strong>No fans found</strong><span>Kelvin couldn\'t find any fan headers it can read.</span></div>'; return; }
    if (!fan.controllable) {
      el.innerHTML = state.laptop
        ? `<div class="empty"><strong>${esc(fan.name)}: ${fmtRpm(fan.rpm)} rpm</strong><span>Your laptop runs this fan itself. Use the laptop fan mode above to make it quieter or stronger.</span></div>`
        : `<div class="empty"><strong>${esc(fan.name)} is read only</strong><span>Kelvin can see its speed, but this fan doesn't accept speed changes.</span></div>`;
      return;
    }

    const d = draft;
    const sensors = temperatureSensors(state);
    const p = fan.profile;
    el.innerHTML = `
      <div class="panel-head"><h2>${esc(fan.name)}</h2><span class="meta" id="fx-live"></span></div>
      <div class="panel-body editor">
        <div class="field">
          <span class="label" id="mode-l">Who controls this fan</span>
          <div class="segmented" role="group" aria-labelledby="mode-l" id="fx-mode">
            ${[['bios', 'BIOS'], ['fixed', 'Fixed speed'], ['curve', 'Curve']].map(([v, l]) => `<button type="button" data-v="${v}" aria-pressed="${d.mode === v}">${l}</button>`).join('')}
          </div>
          <span class="hint">${d.mode === 'bios' ? 'Your motherboard\'s own settings control this fan. Kelvin only watches.'
            : d.mode === 'fixed' ? 'The fan runs at one speed no matter the temperature.'
            : 'The fan speeds up as the temperature you pick rises.'}</span>
        </div>
        ${d.mode === 'fixed' ? `
          <div class="field"><label for="fx-fixed">Speed</label>
            <div class="slider"><input type="range" id="fx-fixed" min="${fan.isPump ? 50 : 0}" max="100" value="${d.fixedPercent}"><output>${Math.round(d.fixedPercent)}%</output></div></div>` : ''}
        ${d.mode === 'curve' ? `
          <div class="field-row">
            <div class="field"><span class="label" id="preset-l">Curve</span>
              <div class="segmented" role="group" aria-labelledby="preset-l" id="fx-preset">
                ${['silent', 'balanced', 'performance', 'custom'].map((v) => `<button type="button" data-v="${v}" aria-pressed="${d.preset === v}">${v[0].toUpperCase() + v.slice(1)}</button>`).join('')}
              </div></div>
            <div class="field"><label for="fx-source">Follows</label>
              <select id="fx-source">${sensors.map((s) => `<option value="${esc(s.id)}" ${s.id === d.sourceSensorId ? 'selected' : ''}>${esc(sensorLabel(s))}</option>`).join('')}</select></div>
          </div>
          <div id="fx-curve"></div>
          <div class="field-row">
            <div class="field"><label for="fx-min">Never slower than</label>
              <div class="slider"><input type="range" id="fx-min" min="0" max="60" value="${d.minPercent}"><output>${Math.round(d.minPercent)}%</output></div>
              <span class="hint">${p?.stopPercent != null ? `This fan stalls at ${p.stopPercent}% or below.` : 'Keeps the fan from stalling at low speed.'}</span></div>
            <div class="field"><label for="fx-resp">Reaction to spikes</label>
              <div class="slider"><input type="range" id="fx-resp" min="1" max="20" value="${d.responseSeconds}"><output>${d.responseSeconds}s</output></div>
              <span class="hint">Slower ignores short spikes, so the fan doesn't rev every time an app opens.</span></div>
          </div>` : ''}
        ${p ? `
          <div class="field"><span class="label">Measured</span>
            <div class="measure">
              <div><span>Range</span><strong>${p.connected ? `${fmtRpm(p.minRpm)}–${fmtRpm(p.maxRpm)} rpm` : 'Not spinning'}</strong></div>
              <div><span>Stalls at</span><strong>${p.stopPercent != null ? `${p.stopPercent}%` : 'Never'}</strong></div>
              <div><span>Measured</span><strong>${new Date(p.measuredAt).toLocaleDateString()}</strong></div>
            </div></div>` : ''}
      </div>
      <div class="editor-foot"><span class="state" id="fx-state"></span>
        <div class="actions"><button class="btn ghost" id="fx-revert">Revert</button><button class="btn primary" id="fx-save">Save</button></div></div>`;

    el.querySelector('#fx-mode').addEventListener('click', (e) => {
      const b = e.target.closest('button'); if (!b) return;
      draft.mode = b.dataset.v; renderEditor();
    });
    el.querySelector('#fx-preset')?.addEventListener('click', (e) => {
      const b = e.target.closest('button'); if (!b) return;
      draft.preset = b.dataset.v;
      if (b.dataset.v !== 'custom') draft.points = clone(state.presets[b.dataset.v]);
      renderEditor();
    });
    bindSlider('#fx-fixed', (v) => { draft.fixedPercent = v; return `${v}%`; });
    bindSlider('#fx-min', (v) => { draft.minPercent = v; return `${v}%`; });
    bindSlider('#fx-resp', (v) => { draft.responseSeconds = v; return `${v}s`; });
    el.querySelector('#fx-source')?.addEventListener('change', (e) => { draft.sourceSensorId = e.target.value; syncFoot(); });
    el.querySelector('#fx-revert').addEventListener('click', () => { draft = clone(base); renderEditor(); });
    el.querySelector('#fx-save').addEventListener('click', save);

    curve = null;
    const host = el.querySelector('#fx-curve');
    if (host) {
      curve = createCurveEditor(host, {
        points: draft.points,
        onChange(points) {
          draft.points = points;
          if (draft.preset !== 'custom') { draft.preset = 'custom'; el.querySelectorAll('#fx-preset button').forEach((b) => b.setAttribute('aria-pressed', b.dataset.v === 'custom')); }
          syncFoot();
        },
      });
    }
    syncFoot();
    updateLive();

    function bindSlider(sel, apply) {
      const input = el.querySelector(sel);
      if (!input) return;
      input.addEventListener('input', () => { input.nextElementSibling.textContent = apply(Number(input.value)); syncFoot(); });
    }
  }

  function syncFoot() {
    const dirty = !same(draft, base);
    const st = root.querySelector('#fx-state');
    if (!st) return;
    st.textContent = dirty ? 'Unsaved changes' : 'Saved';
    root.querySelector('#fx-save').disabled = !dirty;
    root.querySelector('#fx-revert').disabled = !dirty;
  }

  async function save() {
    const btn = root.querySelector('#fx-save');
    btn.disabled = true;
    try {
      const saved = await api('PUT', '/fans', draft);
      base = clone(saved); draft = clone(saved);
      toast('Fan settings saved');
      renderEditor();
    } catch (e) { toast(e.message, true); btn.disabled = false; }
  }

  function updateLive() {
    const fan = lastState.fans.find((f) => f.id === selected);
    const live = root.querySelector('#fx-live');
    if (!fan || !live) return;
    live.textContent = `${fmtRpm(fan.rpm)} rpm · ${fmtPct(fan.percent)}`;
    if (curve) {
      const src = lastState.sensors.find((s) => s.id === draft.sourceSensorId);
      curve.setLive(src?.value, fan.percent);
    }
  }

  function select(id) {
    const fans = lastState.fans;
    selected = fans.some((f) => f.id === id) ? id : (fans.find((f) => f.controllable && f.profile?.connected !== false) ?? fans[0])?.id;
    const fan = fans.find((f) => f.id === selected);
    profileKey = JSON.stringify(fan?.profile);
    listKey = '';
    base = fan ? settingsFor(fan) : null;
    draft = base ? clone(base) : null;
    renderList(lastState);
    renderEditor();
  }

  return {
    update(state) {
      const first = !lastState;
      lastState = state;
      const b = banners(state);
      if (b !== bannerKey) { root.querySelector('#fx-banners').innerHTML = b; bannerKey = b; }
      renderLaptop(state);
      root.querySelector('#fx-calibrate').hidden = !state.fans.some((f) => f.controllable);
      root.querySelector('#fx-calibrate').disabled = state.calibration.running;
      root.querySelector('#fx-calibrate').textContent = state.calibration.running ? `Measuring… ${Math.round(state.calibration.progress * 100)}%` : 'Measure fans';
      if (first) { select(selected); return; }
      renderList(state);
      // Pick up changes saved elsewhere (e.g. from Advice) without throwing away an edit in progress.
      const fan = state.fans.find((f) => f.id === selected);
      if (fan) {
        const server = settingsFor(fan);
        const pk = JSON.stringify(fan.profile);
        if (pk !== profileKey || !same(server, base)) {
          const clean = same(draft, base);
          profileKey = pk;
          base = server;
          if (clean) draft = clone(server);
          renderEditor();
        }
      }
      updateLive();
    },
    setParam(p) { if (lastState) select(p); else selected = p; },
  };
}

// ---------------------------------------------------------------- Lighting

const SWATCHES = ['#ff8a3d', '#ff3b30', '#ff5fa2', '#9b6bff', '#7aa7ff', '#3ddc97', '#ffffff'];
const EFFECTS = [
  ['off', 'Off'], ['static', 'Static'], ['breathing', 'Breathing'], ['spectrum', 'Spectrum'], ['temperature', 'Temperature'],
];

const COLOR_MODES = [['follow', 'Follow effect'], ['solid', 'Solid color'], ['gradient', 'Gradient'], ['off', 'Off']];

// Color settings for one device or zone: a mode, plus one or two colors when it has its own.
function colorControls(own, devId, zone, label) {
  const mode = own?.mode ?? 'follow';
  return `<div class="color-ctl" data-dev="${esc(devId)}" data-zone="${esc(zone ?? '')}">
    <select class="sm" aria-label="Colors for ${esc(label)}">
      ${COLOR_MODES.map(([v, l]) => `<option value="${v}" ${v === mode ? 'selected' : ''}>${l}</option>`).join('')}
    </select>
    ${mode === 'solid' || mode === 'gradient' ? `<input type="color" class="cpick" data-c="color" value="${esc(own.color)}" aria-label="${mode === 'gradient' ? 'Start color' : 'Color'} for ${esc(label)}">` : ''}
    ${mode === 'gradient' ? `<span class="faint" aria-hidden="true">→</span><input type="color" class="cpick" data-c="color2" value="${esc(own.color2)}" aria-label="End color for ${esc(label)}">` : ''}
  </div>`;
}

function lightingView(root) {
  root.innerHTML = `<div class="page">
    <header class="page-head"><div><h1>Lighting</h1><p>One effect across every device, or give any device or zone its own colors. Changes apply as you make them.</p></div></header>
    <div class="stack" id="lx-banners"></div>
    <div id="lx-body"></div>
  </div>`;

  let draft = null;
  let editingUntil = 0;
  let lastState = null;
  let structureKey = '';
  let bannerKey = '';
  const expanded = new Set(); // devices whose zones are shown

  const push = debounce(async () => {
    try { await api('PUT', '/lighting', draft); } catch (e) { toast(e.message, true); }
  }, 120);

  function change(mutate, rerender = false) {
    mutate(draft);
    editingUntil = Date.now() + 1500;
    push();
    if (rerender) render();
  }

  function render() {
    const state = lastState;
    const body = root.querySelector('#lx-body');
    if (!state.system.rgbConnected) {
      const s = lightingStatus(state);
      body.innerHTML = `<section class="panel"><div class="empty" ${s.busy ? 'aria-busy="true"' : ''}>${icon(s.busy ? 'lighting' : 'plug', 24)}
        <strong>${esc(s.title)}</strong>
        <span style="max-width:62ch">${esc(s.detail)}</span>
        ${s.busy ? '<div class="progress indeterminate" style="width:220px;margin-top:8px"><i></i></div>' : ''}</div></section>`;
      return;
    }
    const d = draft;
    const animated = d.effect === 'breathing' || d.effect === 'spectrum';
    const usesColor = d.effect === 'static' || d.effect === 'breathing';
    const sensors = temperatureSensors(state);
    const swatchPressed = SWATCHES.includes(d.color.toLowerCase());

    body.innerHTML = `<div class="grid-2 even">
      <section class="panel">
        <div class="panel-head"><h2>Effect</h2></div>
        <div class="panel-body effect-controls">
          <div class="segmented" role="group" aria-label="Effect" id="lx-effect">
            ${EFFECTS.map(([v, l]) => `<button type="button" data-v="${v}" aria-pressed="${d.effect === v}">${l}</button>`).join('')}
          </div>
          ${usesColor ? `<div class="field"><span class="label">Color</span>
            <div class="swatches" id="lx-swatches">
              ${SWATCHES.map((c) => `<button class="swatch" style="background:${c}" data-c="${c}" aria-label="Color ${c}" aria-pressed="${d.color.toLowerCase() === c}"></button>`).join('')}
              <label class="custom-color" title="Pick any color" aria-label="Pick any color" ${!swatchPressed ? `style="background:${d.color};border-style:solid"` : ''}>
                ${swatchPressed ? icon('plus', 16) : ''}<input type="color" id="lx-custom" value="${d.color}"></label>
            </div></div>` : ''}
          ${d.effect === 'temperature' ? `<div class="field"><label for="lx-sensor">Follows</label>
            <select id="lx-sensor">${sensors.map((s) => `<option value="${esc(s.id)}" ${s.id === d.temperatureSensorId ? 'selected' : ''}>${esc(sensorLabel(s))}</option>`).join('')}</select>
            <span class="hint">Blue when cool, warm white in between, amber then red as it heats up. You can read your temperature from across the room.</span></div>` : ''}
          ${d.effect !== 'off' ? `<div class="field"><label for="lx-bright">Brightness</label>
            <div class="slider"><input type="range" id="lx-bright" min="0" max="100" value="${Math.round(d.brightness * 100)}"><output>${Math.round(d.brightness * 100)}%</output></div></div>` : ''}
          ${animated ? `<div class="field"><label for="lx-speed">Speed</label>
            <div class="slider"><input type="range" id="lx-speed" min="0" max="100" value="${Math.round(d.speed * 100)}"><output>${Math.round(d.speed * 100)}%</output></div></div>` : ''}
        </div>
      </section>
      <section class="panel">
        <div class="panel-head"><h2>Devices</h2><span class="meta">Turn one off to let another app drive it</span></div>
        <div id="lx-devices">${state.devices.map((dev) => {
          const own = d.devices?.[dev.id];
          const zones = dev.zones ?? [];
          const open = expanded.has(dev.id);
          return `<div class="device-block ${dev.included ? '' : 'off'}" data-id="${esc(dev.id)}">
            <div class="device ${dev.included ? '' : 'off'}" data-id="${esc(dev.id)}">
              <div><strong>${esc(dev.name)}</strong><span class="faint">${esc(dev.vendor)} · ${esc(dev.kind)} · ${dev.ledCount} LEDs</span></div>
              ${ledStrip(state.preview[dev.id], dev.ledCount)}
              <label class="switch" title="${dev.included ? 'Kelvin controls this device' : 'Kelvin leaves this device alone'}">
                <input type="checkbox" class="include" ${dev.included ? 'checked' : ''} aria-label="Kelvin controls ${esc(dev.name)}"><span></span></label>
            </div>
            ${dev.included ? `<div class="device-sub">
              ${colorControls(own, dev.id, null, dev.name)}
              ${zones.length > 1 ? `<button type="button" class="btn sm ghost" data-zones aria-expanded="${open}">${open ? 'Hide zones' : `Zones (${zones.length})`}</button>` : ''}
            </div>
            ${open ? `<div class="zone-list">${zones.map((z) => `
              <div class="zone-row"><span>${esc(z.name)} <span class="faint">· ${z.ledCount} LED${z.ledCount === 1 ? '' : 's'}</span></span>
                ${colorControls(own?.zones?.[z.name], dev.id, z.name, `${dev.name} ${z.name}`)}</div>`).join('')}</div>` : ''}` : ''}
          </div>`;
        }).join('') || '<div class="empty"><strong>No lighting devices found</strong><span>OpenRGB is running but didn\'t report any devices.</span></div>'}
        </div>
      </section>
    </div>`;

    body.querySelector('#lx-effect').addEventListener('click', (e) => {
      const b = e.target.closest('button'); if (b) change((x) => { x.effect = b.dataset.v; }, true);
    });
    body.querySelector('#lx-swatches')?.addEventListener('click', (e) => {
      const b = e.target.closest('.swatch'); if (b) change((x) => { x.color = b.dataset.c; }, true);
    });
    body.querySelector('#lx-custom')?.addEventListener('change', (e) => change((x) => { x.color = e.target.value; }, true));
    body.querySelector('#lx-custom')?.addEventListener('input', (e) => change((x) => { x.color = e.target.value; }));
    body.querySelector('#lx-sensor')?.addEventListener('change', (e) => change((x) => { x.temperatureSensorId = e.target.value; }));
    for (const [sel, key] of [['#lx-bright', 'brightness'], ['#lx-speed', 'speed']]) {
      const input = body.querySelector(sel);
      input?.addEventListener('input', () => {
        input.nextElementSibling.textContent = `${input.value}%`;
        change((x) => { x[key] = Number(input.value) / 100; });
      });
    }
    const list = body.querySelector('#lx-devices');
    list.addEventListener('change', (e) => {
      if (e.target.matches('.include')) {
        const id = e.target.closest('.device-block').dataset.id;
        change((x) => {
          x.excludedDeviceIds = x.excludedDeviceIds.filter((v) => v !== id);
          if (!e.target.checked) x.excludedDeviceIds.push(id);
        }, true);
        return;
      }
      const ctl = e.target.closest('.color-ctl');
      if (ctl && e.target.matches('select')) {
        change((x) => { ownColors(x, ctl.dataset.dev, ctl.dataset.zone || null).mode = e.target.value; }, true);
      }
    });
    list.addEventListener('input', (e) => {
      const ctl = e.target.closest('.color-ctl');
      if (ctl && e.target.matches('.cpick')) {
        change((x) => { ownColors(x, ctl.dataset.dev, ctl.dataset.zone || null)[e.target.dataset.c] = e.target.value; });
      }
    });
    list.addEventListener('click', (e) => {
      const b = e.target.closest('[data-zones]'); if (!b) return;
      const id = b.closest('.device-block').dataset.id;
      if (expanded.has(id)) expanded.delete(id); else expanded.add(id);
      render();
    });
  }

  // The color settings for a device, or one of its zones, created on first use. New overrides
  // start from the current effect color, so switching to "Solid" doesn't jump to a random color.
  function ownColors(x, devId, zone) {
    x.devices ??= {};
    const dev = x.devices[devId] ??= { mode: 'follow', color: x.color, color2: '#3d7bff', zones: {} };
    dev.zones ??= {};
    if (zone == null) return dev;
    return dev.zones[zone] ??= { mode: 'follow', color: dev.mode === 'follow' ? x.color : dev.color, color2: dev.color2 ?? '#3d7bff' };
  }

  return {
    update(state) {
      lastState = state;
      const b = banners(state);
      if (b !== bannerKey) { root.querySelector('#lx-banners').innerHTML = b; bannerKey = b; }
      // The server wins unless the user touched something in the last moment.
      const editing = Date.now() < editingUntil;
      if (!draft || (!editing && !same(draft, state.lighting))) draft = clone(state.lighting);
      const key = JSON.stringify([state.system.rgbConnected, state.system.openRgb, state.devices.map((d) => [d.id, d.included]), draft]);
      if (!editing && key !== structureKey) { structureKey = key; render(); }
      for (const d of state.devices) updateLeds(root.querySelector(`.device[data-id="${CSS.escape(d.id)}"] .leds`), state.preview[d.id]);
    },
  };
}

// ---------------------------------------------------------------- Profiles

const FAN_MODE_LABEL = { quiet: 'Quiet', balanced: 'Balanced', performance: 'Performance', max: 'Max fan' };

function profilesView(root) {
  root.innerHTML = `<div class="page">
    <header class="page-head"><div><h1>Profiles</h1>
      <p>Each profile is a complete setup: fan curves, lighting${''} and, on laptops, the fan mode. Changes you make on the Fans and Lighting pages save into the active profile.</p></div>
      <div class="actions"><button class="btn" id="pf-new">${icon('plus', 16)} New from current settings</button></div></header>
    <section class="panel"><div id="pf-list"></div></section>
    <section class="panel">
      <div class="panel-head"><h2>Switch automatically</h2>
        <label class="switch" title="Automatic switching"><input type="checkbox" id="pf-auto" aria-label="Switch profiles automatically"><span></span></label></div>
      <div class="panel-body stack">
        <p class="muted" id="pf-status"></p>
        <div class="field-row">
          <div class="field"><label for="pf-default">Normally use</label><select id="pf-default"></select></div>
        </div>
        <div class="auto-rule">
          <h3>When a game is running</h3>
          <div class="field-row">
            <div class="field"><label for="pf-game">Switch to</label><select id="pf-game"></select></div>
            <div class="field"><span class="label">How Kelvin spots games</span>
              <label class="check"><input type="checkbox" id="pf-gpu"> When the graphics card is busy (70%+ for 30 seconds)</label></div>
          </div>
          <div class="field"><label for="pf-proc">These apps always count as games</label>
            <div class="chips" id="pf-procs"></div>
            <div class="actions"><input type="text" id="pf-proc" placeholder="e.g. eldenring or Cyberpunk2077.exe" maxlength="100" style="flex:1;max-width:340px">
              <button type="button" class="btn sm" id="pf-proc-add">Add</button></div>
            <span class="hint">Use the program's file name. You'll find it in Task Manager → Details.</span></div>
        </div>
        <div class="auto-rule">
          <h3><label class="check" style="color:var(--text)"><input type="checkbox" id="pf-night"> At night</label></h3>
          <div class="field-row">
            <div class="field"><label for="pf-nightp">Switch to</label><select id="pf-nightp"></select></div>
            <div class="field"><span class="label">Between</span>
              <div class="actions"><input type="time" id="pf-start" aria-label="Night starts"> <span class="muted">and</span> <input type="time" id="pf-end" aria-label="Night ends"></div></div>
          </div>
        </div>
        <p class="faint">A game beats the night schedule. If you pick a profile yourself, it stays until a game starts or ends, or the night window opens or closes.</p>
        <div class="actions"><button class="btn primary" id="pf-save" disabled>Save</button></div>
      </div>
    </section>
  </div>`;

  const $ = (s) => root.querySelector(s);
  let auto = null, autoBase = null, lastState = null, listKey = '';

  const options = (sel, value, allowNone) => {
    $(sel).innerHTML = (allowNone ? '<option value="">Don\'t switch</option>' : '')
      + lastState.profiles.map((p) => `<option value="${esc(p.id)}" ${p.id === value ? 'selected' : ''}>${esc(p.name)}</option>`).join('');
  };

  function renderAuto() {
    $('#pf-auto').checked = auto.enabled;
    options('#pf-default', auto.defaultProfileId, false);
    options('#pf-game', auto.gameProfileId ?? '', true);
    options('#pf-nightp', auto.nightProfileId ?? '', true);
    $('#pf-gpu').checked = auto.detectGamesByGpu;
    $('#pf-night').checked = auto.nightEnabled;
    $('#pf-start').value = auto.nightStart;
    $('#pf-end').value = auto.nightEnd;
    $('#pf-procs').innerHTML = auto.gameProcesses.map((p, i) =>
      `<span class="chip">${esc(p)}<button type="button" data-rm="${i}" aria-label="Remove ${esc(p)}">${icon('close', 14)}</button></span>`).join('')
      || '<span class="faint">None yet.</span>';
    changed();
  }
  const changed = () => { $('#pf-save').disabled = same(auto, autoBase); };

  root.addEventListener('change', (e) => {
    if (!auto) return;
    const t = e.target;
    if (t.id === 'pf-auto') auto.enabled = t.checked;
    else if (t.id === 'pf-default') auto.defaultProfileId = t.value;
    else if (t.id === 'pf-game') auto.gameProfileId = t.value || null;
    else if (t.id === 'pf-nightp') auto.nightProfileId = t.value || null;
    else if (t.id === 'pf-gpu') auto.detectGamesByGpu = t.checked;
    else if (t.id === 'pf-night') auto.nightEnabled = t.checked;
    else if (t.id === 'pf-start') auto.nightStart = t.value;
    else if (t.id === 'pf-end') auto.nightEnd = t.value;
    else return;
    changed();
  });
  const addProc = () => {
    const v = $('#pf-proc').value.trim().replace(/\.exe$/i, '');
    if (!v) return;
    if (!auto.gameProcesses.some((p) => p.toLowerCase() === v.toLowerCase())) auto.gameProcesses.push(v);
    $('#pf-proc').value = '';
    renderAuto();
  };
  $('#pf-proc-add').addEventListener('click', addProc);
  $('#pf-proc').addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); addProc(); } });
  $('#pf-procs').addEventListener('click', (e) => {
    const b = e.target.closest('[data-rm]'); if (!b) return;
    auto.gameProcesses.splice(Number(b.dataset.rm), 1); renderAuto();
  });
  $('#pf-save').addEventListener('click', () => run(async () => {
    const saved = await api('PUT', '/profiles/automation', auto);
    auto = clone(saved); autoBase = clone(saved); renderAuto();
  }, 'Automatic switching saved'));

  $('#pf-new').addEventListener('click', () => run(async () => {
    const name = prompt('Name for the new profile (it starts as a copy of your current settings):');
    if (!name) return;
    await api('POST', '/profiles', { name });
  }));

  $('#pf-list').addEventListener('click', (e) => {
    const row = e.target.closest('[data-profile]'); if (!row) return;
    const id = row.dataset.profile;
    const act = e.target.closest('[data-act]')?.dataset.act;
    if (act === 'use') run(() => api('PUT', '/profiles/active', { id }));
    if (act === 'rename') {
      const name = prompt('New name:', lastState.profiles.find((p) => p.id === id)?.name ?? '');
      if (name) run(() => api('PUT', `/profiles/${encodeURIComponent(id)}`, { name }), 'Renamed');
    }
    if (act === 'delete' && confirm('Delete this profile? Its fan and lighting settings are lost.')) {
      run(() => api('DELETE', `/profiles/${encodeURIComponent(id)}`), 'Profile deleted');
    }
  });
  $('#pf-list').addEventListener('change', (e) => {
    const sel = e.target.closest('select[data-fanmode]'); if (!sel) return;
    run(() => api('PUT', `/profiles/${encodeURIComponent(sel.dataset.fanmode)}`, { fanMode: sel.value }), 'Fan mode saved');
  });

  return {
    update(state) {
      lastState = state;
      const st = state.profileStatus ?? {};
      $('#pf-status').innerHTML = `Now using <strong>${esc(state.profiles.find((p) => p.id === state.activeProfileId)?.name ?? '–')}</strong>: ${esc(st.reason ?? '')}`;

      const key = JSON.stringify([state.profiles, state.activeProfileId, state.laptop?.modes, state.automation]);
      if (key !== listKey) {
        listKey = key;
        const usedBy = (id) => [
          state.automation.defaultProfileId === id && 'normally',
          state.automation.gameProfileId === id && 'for games',
          state.automation.nightEnabled && state.automation.nightProfileId === id && 'at night',
        ].filter(Boolean);
        $('#pf-list').innerHTML = state.profiles.map((p) => {
          const active = p.id === state.activeProfileId;
          const roles = usedBy(p.id);
          return `<div class="profile-row ${active ? 'active' : ''}" data-profile="${esc(p.id)}">
            <div><strong>${esc(p.name)}</strong> ${active ? '<span class="tag accent">Active</span>' : ''}
              <div class="faint">${roles.length ? `Used ${roles.join(', ')}` : 'Only when you pick it'}</div></div>
            ${state.laptop ? `<label class="fanmode"><span class="faint">Laptop fan mode</span>
              <select data-fanmode="${esc(p.id)}" aria-label="Laptop fan mode for ${esc(p.name)}">
                ${state.laptop.modes.map((m) => `<option value="${m.id}" ${m.id === p.fanMode ? 'selected' : ''}>${esc(m.label)}</option>`).join('')}
              </select></label>` : '<span></span>'}
            <div class="actions">
              ${active ? '' : '<button class="btn sm primary" data-act="use">Use now</button>'}
              <button class="btn sm ghost" data-act="rename">Rename</button>
              <button class="btn sm ghost" data-act="delete" ${roles.length || state.profiles.length <= 1 ? 'disabled title="Used by automatic switching"' : ''}>Delete</button>
            </div></div>`;
        }).join('');
      }

      if (!auto || (same(auto, autoBase) && !same(autoBase, state.automation))) {
        auto = clone(state.automation); autoBase = clone(state.automation); renderAuto();
      }
    },
  };
}

// ---------------------------------------------------------------- Health

const BAND_LABEL = { idle: 'Idle', light: 'Light load', medium: 'Medium load', heavy: 'Heavy load' };
const STATUS_TAG = {
  warmer: ['warn', 'Running hotter'],
  cooler: ['ok', 'Running cooler'],
  ok: ['ok', 'Healthy'],
  learning: ['', 'Learning'],
  settingsChanged: ['', 'Re-learning'],
};

// Daily temperature-above-board for the most common load, with "then" and "now" windows shaded.
function healthChart(part) {
  const pts = part.trend;
  if (pts.length < 2) return '<p class="faint">The chart appears after a couple of days of normal use.</p>';
  // Drawn at roughly the size it's shown, so labels stay at body-text size on wide screens.
  const W = 1040, H = 240, L = 44, R = 12, T = 20, B = 28;
  const day = (d) => Date.parse(d) / 86400000;
  const today = Math.floor(Date.now() / 86400000);
  const x0 = Math.min(day(pts[0].date), today - 45), x1 = today;
  const vals = pts.map((p) => p.rise);
  const lo = Math.floor(Math.min(...vals) - 2), hi = Math.ceil(Math.max(...vals) + 2);
  const x = (d) => L + ((d - x0) / Math.max(x1 - x0, 1)) * (W - L - R);
  const y = (v) => T + (1 - (v - lo) / Math.max(hi - lo, 1)) * (H - T - B);
  const span = (a, b, label) => `<rect x="${x(a)}" y="${T}" width="${Math.max(x(b) - x(a), 0)}" height="${H - T - B}" fill="#7aa7ff" fill-opacity="0.07"/>
    <text x="${(x(a) + x(b)) / 2}" y="${T - 6}" fill="#8189a3" font-size="11.5" text-anchor="middle">${label}</text>`;
  let grid = '';
  const step = Math.max(2, Math.round((hi - lo) / 4));
  for (let v = Math.ceil(lo / step) * step; v <= hi; v += step) {
    grid += `<line x1="${L}" x2="${W - R}" y1="${y(v)}" y2="${y(v)}" stroke="#252a39"/><text x="${L - 8}" y="${y(v) + 4}" fill="#8189a3" font-size="11.5" text-anchor="end">+${v}°</text>`;
  }
  const line = pts.map((p, i) => `${i ? 'L' : 'M'}${x(day(p.date)).toFixed(1)},${y(p.rise).toFixed(1)}`).join(' ');
  const last = pts.at(-1);
  const color = part.status === 'warmer' ? '#f2b84b' : '#7aa7ff';
  return `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(part.name)} temperature above the motherboard, by day">
    ${span(today - 45, today - 14, 'then')}${span(today - 7, today, 'now')}
    ${grid}
    <path d="${line}" fill="none" stroke="${color}" stroke-width="2" stroke-linejoin="round"/>
    <circle cx="${x(day(last.date))}" cy="${y(last.rise)}" r="4" fill="${color}"/>
    <text x="${L}" y="${H - 6}" fill="#8189a3" font-size="11.5">${new Date(Date.parse(pts[0].date)).toLocaleDateString(undefined, { day: 'numeric', month: 'short' })}</text>
    <text x="${W - R}" y="${H - 6}" fill="#8189a3" font-size="11.5" text-anchor="end">today</text>
  </svg>`;
}

function healthView(root) {
  root.innerHTML = `<div class="page">
    <header class="page-head"><div><h1>Health</h1>
      <p>Kelvin compares how hot your parts run at the same load, week by week, to catch dust build-up or drying thermal paste before it becomes a problem.</p></div></header>
    <div class="stack" id="hx-parts"></div>
  </div>`;
  let key = '';

  return {
    update(state) {
      const h = state.health;
      const k = JSON.stringify(h?.parts);
      if (k === key) return;
      key = k;
      const host = root.querySelector('#hx-parts');
      if (!h) { host.innerHTML = '<section class="panel"><div class="empty"><strong>Checking…</strong></div></section>'; return; }
      host.innerHTML = h.parts.map((p) => {
        const [tone, label] = STATUS_TAG[p.status] ?? ['', p.status];
        const learning = p.status === 'learning' && p.daysCollected < p.daysNeeded;
        const unit = p.relativeToBoard ? '°C above motherboard' : '°C';
        return `<section class="panel">
          <div class="panel-head"><h2>${esc(p.name)}</h2><span class="tag ${tone}">${label}</span></div>
          <div class="panel-body stack" style="gap:18px">
            <div><strong class="health-headline">${esc(p.headline)}</strong><p class="muted" style="margin-top:4px;max-width:75ch">${esc(p.detail)}</p></div>
            ${learning ? `<div class="field"><span class="label">Day ${p.daysCollected} of ${p.daysNeeded}</span>
              <div class="progress"><i style="width:${(p.daysCollected / p.daysNeeded) * 100}%"></i></div></div>` : ''}
            ${p.trendBand ? `<div class="field"><span class="label">Daily average at ${esc((BAND_LABEL[p.trendBand] ?? p.trendBand).toLowerCase())}, ${unit}</span>
              <div class="health-chart">${healthChart(p)}</div></div>` : ''}
            ${p.bands.length ? `<table class="health-table"><thead><tr><th>Load</th><th class="num">Then</th><th class="num">Now</th><th class="num">Change</th></tr></thead><tbody>
              ${p.bands.map((b) => `<tr>
                <td>${BAND_LABEL[b.band] ?? b.band}</td>
                <td class="num">+${b.thenRise.toFixed(1)}°</td>
                <td class="num">+${b.nowRise.toFixed(1)}°</td>
                <td class="num">${b.compared
                  ? `<span class="${b.change >= 4 ? 'delta-up' : b.change <= -4 ? 'delta-down' : ''}">${b.change > 0 ? '+' : ''}${b.change.toFixed(1)}°</span>`
                  : `<span class="faint">${esc(b.skipped)}</span>`}</td></tr>`).join('')}
            </tbody></table>` : ''}
          </div></section>`;
      }).join('');
    },
  };
}

// ---------------------------------------------------------------- Advice

function adviceView(root) {
  root.innerHTML = `<div class="page">
    <header class="page-head"><div><h1>Advice</h1><p>Suggestions based on the fans and sensors Kelvin found in this PC, with the reason for each.</p></div>
      <div class="actions"><button class="btn primary" id="ax-all" hidden>Apply all fan suggestions</button></div></header>
    <section class="panel" id="ax-cal" hidden></section>
    <section class="panel" id="ax-list"></section>
  </div>`;

  let key = '';
  root.querySelector('#ax-all').addEventListener('click', () =>
    run(async () => { const r = await api('POST', '/recommendations/apply-all'); toast(`Applied ${r.applied} fan setting${r.applied === 1 ? '' : 's'}`); }));

  root.querySelector('#ax-list').addEventListener('click', (e) => {
    const b = e.target.closest('button[data-act]'); if (!b) return;
    const id = b.closest('.rec').dataset.id;
    if (b.dataset.act === 'dismiss') run(() => api('POST', '/recommendations/dismiss', { id }), 'Dismissed');
    if (b.dataset.act === 'apply') run(() => api('POST', '/recommendations/apply', { id }), 'Applied');
    if (b.dataset.act === 'calibrate') run(() => api('POST', '/fans/calibrate'), 'Measuring fans. This takes about 30 seconds.');
    if (b.dataset.act === 'open') go('fans', b.dataset.fan);
  });

  return {
    update(state) {
      const cal = state.calibration;
      const calEl = root.querySelector('#ax-cal');
      calEl.hidden = !cal.running && !cal.message;
      calEl.innerHTML = cal.running
        ? `<div class="panel-body stack" style="gap:10px"><div class="actions" style="justify-content:space-between"><strong>${esc(cal.message)}</strong><span class="faint">${Math.round(cal.progress * 100)}%</span></div>
           <div class="progress"><i style="width:${cal.progress * 100}%"></i></div>
           <span class="faint">Fans will speed up and slow down. That's expected.</span></div>`
        : `<div class="panel-body"><span class="muted">${esc(cal.message ?? '')}</span></div>`;

      const recs = state.recommendations;
      root.querySelector('#ax-all').hidden = recs.filter((r) => r.action === 'applyFan').length < 2;
      const k = JSON.stringify([recs, cal.running]);
      if (k === key) return;
      key = k;
      const list = root.querySelector('#ax-list');
      if (!recs.length) {
        list.innerHTML = `<div class="empty">${icon('check', 24)}<strong>Nothing to suggest</strong><span>Your fans are set up the way Kelvin would set them.</span></div>`;
        return;
      }
      list.innerHTML = recs.map((r) => `
        <div class="rec ${r.severity}" data-id="${esc(r.id)}">
          <span class="icon">${icon(r.severity === 'warning' ? 'alert' : r.severity === 'suggestion' ? 'advice' : 'info')}</span>
          <div><strong>${esc(r.title)}</strong><p>${esc(r.detail)}</p></div>
          <div class="actions">
            ${r.action === 'calibrate' ? `<button class="btn primary sm" data-act="calibrate" ${cal.running ? 'disabled' : ''}>Measure fans</button>` : ''}
            ${r.action === 'applyFan' ? `<button class="btn sm ghost" data-act="open" data-fan="${esc(r.fanId)}">Preview</button><button class="btn primary sm" data-act="apply">Apply</button>` : ''}
            ${r.action === 'closeApp' ? `<button class="btn primary sm" data-close-conflicts="${esc(r.id.slice('conflict:'.length))}" data-names="${esc(r.title.replace(/ is running$/, ''))}">Close for me</button>` : ''}
            ${r.action !== 'calibrate' && r.action !== 'closeApp' ? '<button class="btn sm ghost" data-act="dismiss">Dismiss</button>' : ''}
          </div>
        </div>`).join('');
    },
  };
}

// ---------------------------------------------------------------- Settings

function settingsView(root) {
  root.innerHTML = `<div class="page">
    <header class="page-head"><div><h1>Settings</h1></div></header>
    <section class="panel">
      <div class="panel-head"><h2>Safety</h2></div>
      <div class="panel-body stack">
        <p class="muted">If a temperature reaches these limits, every fan goes to full speed until it cools down, whatever its curve says.</p>
        <div class="field-row">
          <div class="field"><label for="st-cpu">CPU limit (°C)</label><input type="number" id="st-cpu" min="70" max="95"></div>
          <div class="field"><label for="st-gpu">GPU limit (°C)</label><input type="number" id="st-gpu" min="70" max="100"></div>
        </div>
        <p class="faint">If Kelvin stops or crashes, every fan goes back to your BIOS settings.</p>
        <div class="actions"><button class="btn primary" id="st-save">Save limits</button></div>
      </div>
    </section>
    <section class="panel" id="alerts">
      <div class="panel-head"><h2>Temperature alerts</h2>
        <label class="switch" title="Temperature alerts on or off"><input type="checkbox" id="al-enabled" aria-label="Temperature alerts on"><span></span></label></div>
      <div class="panel-body stack">
        <p class="muted" style="max-width:72ch">Get a notification when a temperature stays above a limit you choose. Unlike the safety limits above, alerts only tell you; they don't change your fans.</p>
        <div id="al-rules" class="alert-rules"></div>
        <div><button type="button" class="btn sm ghost" id="al-add">${icon('plus', 16)} Add alert</button></div>
        <label class="check"><input type="checkbox" id="al-safety"> Also notify me when a safety limit forces the fans to full speed</label>
        <label class="check"><input type="checkbox" id="al-health"> Also notify me when Health finds a part running hotter than a few weeks ago</label>
        <div class="actions">
          <button class="btn primary" id="al-save" disabled>Save alerts</button>
          <button class="btn" id="al-test">Send a test notification</button>
        </div>
        <p class="faint" id="al-where"></p>
        <div id="al-recent"></div>
      </div>
    </section>
    <section class="panel" id="st-app" hidden>
      <div class="panel-head"><h2>App</h2></div>
      <div class="device">
        <div><strong>Start with Windows</strong><span class="faint">Kelvin opens in the tray when you sign in, so your fan curves run from the start.</span></div>
        <span></span>
        <label class="switch"><input type="checkbox" id="st-autostart" aria-label="Start Kelvin with Windows"><span></span></label>
      </div>
      <div class="device">
        <div><strong>Closing the window</strong><span class="faint">Kelvin stays in the tray. Your fan curves and lighting are run by Kelvin's background service, so they keep working even after you quit the app. If that service ever stops, fans go back to your BIOS settings.</span></div>
      </div>
    </section>
    <section class="panel" id="ha">
      <div class="panel-head"><h2>Home Assistant</h2><span class="meta" id="ha-status"></span></div>
      <div class="panel-body stack">
        <p class="muted" style="max-width:72ch">Share temperatures, fan speeds and your profile with Home Assistant (or any MQTT tool). Kelvin appears as a device on its own through MQTT discovery. Home Assistant can switch profiles, but it can never set fan speeds directly.</p>
        <label class="check"><input type="checkbox" id="ha-enabled"> Connect to an MQTT broker</label>
        <div class="field-row">
          <div class="field"><label for="ha-host">Broker address</label><input type="text" id="ha-host" placeholder="homeassistant.local or 192.168.1.20" maxlength="255"></div>
          <div class="field"><label for="ha-port">Port</label><input type="number" id="ha-port" min="1" max="65535" style="width:110px"></div>
        </div>
        <div class="field-row">
          <div class="field"><label for="ha-user">Username</label><input type="text" id="ha-user" autocomplete="off" maxlength="100"></div>
          <div class="field"><label for="ha-pass">Password</label><input type="password" id="ha-pass" autocomplete="new-password" maxlength="200">
            <span class="hint" id="ha-pass-hint"></span></div>
        </div>
        <label class="check"><input type="checkbox" id="ha-tls"> Use TLS (usually port 8883)</label>
        <label class="check"><input type="checkbox" id="ha-control"> Let Home Assistant switch profiles</label>
        <details><summary class="faint">Advanced</summary>
          <div class="field-row" style="margin-top:10px">
            <div class="field"><label for="ha-base">Topic prefix</label><input type="text" id="ha-base" maxlength="100"></div>
            <div class="field"><label for="ha-disc">Discovery prefix</label><input type="text" id="ha-disc" maxlength="100"></div>
          </div>
        </details>
        <div class="actions"><button class="btn primary" id="ha-save">Save and connect</button><span class="faint" id="ha-topics"></span></div>
      </div>
    </section>
    <section class="panel" id="st-updates" hidden>
      <div class="panel-head"><h2>Updates</h2><span class="meta" id="st-version"></span></div>
      <div class="device">
        <div><strong id="st-update-title">Kelvin keeps itself up to date</strong>
          <span class="faint" id="st-update-detail">It checks GitHub for new versions a few times a day. Every update is signed, and Kelvin refuses any that isn't.</span></div>
        <span></span>
        <div class="actions"><button class="btn sm" id="st-check">Check now</button><button class="btn sm primary" id="st-install" hidden>Update now</button></div>
      </div>
    </section>
    <section class="panel">
      <div class="panel-head"><h2>Hardware</h2></div>
      <table><tbody id="st-hw"></tbody></table>
    </section>
    <section class="panel">
      <div class="panel-head"><h2>Help support more hardware</h2></div>
      <div class="panel-body stack">
        <p class="muted" style="max-width:72ch">Kelvin learns about new boards and devices from the people who use them. Sharing what Kelvin found on your PC helps everyone with the same hardware. You'll see exactly what's included before anything is sent.</p>
        <div class="actions"><button class="btn" id="st-report">Report my hardware</button></div>
        <div id="st-report-preview" class="stack" style="gap:12px" hidden>
          <p class="faint">This is everything that will be shared: model names and readings only. No serial numbers, user names or file paths. It opens as a GitHub issue you can edit before posting.</p>
          <pre class="report" tabindex="0"></pre>
          <div class="actions"><button class="btn primary" id="st-report-open">Open GitHub issue</button><button class="btn" id="st-report-copy">Copy</button></div>
        </div>
      </div>
    </section>
  </div>`;

  // ---- Home Assistant (MQTT)
  let haLoaded = false;
  const $h = (s) => root.querySelector(s);
  $h('#ha-save').addEventListener('click', () => run(async () => {
    const pass = $h('#ha-pass').value;
    await api('PUT', '/mqtt', {
      enabled: $h('#ha-enabled').checked,
      host: $h('#ha-host').value, port: Number($h('#ha-port').value),
      useTls: $h('#ha-tls').checked,
      username: $h('#ha-user').value,
      password: pass === '' ? null : pass, // empty box keeps the saved password
      baseTopic: $h('#ha-base').value, discoveryPrefix: $h('#ha-disc').value,
      allowProfileControl: $h('#ha-control').checked,
    });
    $h('#ha-pass').value = '';
  }, 'Home Assistant settings saved'));

  function updateHa(state) {
    const m = state.mqtt;
    if (!m) return;
    if (!haLoaded) {
      $h('#ha-enabled').checked = m.enabled; $h('#ha-host').value = m.host; $h('#ha-port').value = m.port;
      $h('#ha-user').value = m.username; $h('#ha-tls').checked = m.useTls; $h('#ha-control').checked = m.allowProfileControl;
      $h('#ha-base').value = m.baseTopic; $h('#ha-disc').value = m.discoveryPrefix;
      haLoaded = true;
    }
    $h('#ha-pass-hint').textContent = m.hasPassword ? 'A password is saved. Leave this empty to keep it.' : '';
    $h('#ha-status').innerHTML = m.connected
      ? `<span class="tag ok">Connected</span>`
      : m.enabled ? `<span class="tag warn">${esc(m.status)}</span>` : '<span class="faint">Off</span>';
    $h('#ha-topics').textContent = m.node ? `Publishes to ${m.baseTopic}/${m.node}/…` : '';
  }

  // ---- Temperature alerts editor
  let alerts = null;       // draft
  let alertsBase = null;   // last saved
  const $a = (sel) => root.querySelector(sel);

  function renderRules(state) {
    $a('#al-rules').innerHTML = alerts.rules.map((r, i) => `
      <div class="alert-rule" data-i="${i}">
        <label class="switch" title="${r.enabled ? 'On' : 'Off'}"><input type="checkbox" data-k="enabled" ${r.enabled ? 'checked' : ''} aria-label="Alert ${i + 1} on"><span></span></label>
        <select data-k="source" aria-label="Temperature to watch">
          ${Object.entries(SOURCE_NAME).map(([v, l]) => `<option value="${v}" ${v === r.source ? 'selected' : ''}>${l}</option>`).join('')}
        </select>
        <span class="muted">above</span>
        <input type="number" data-k="threshold" min="40" max="110" step="1" value="${Math.round(r.threshold)}" aria-label="Alert temperature in °C">
        <span class="muted">°C for</span>
        <input type="number" data-k="sustainSeconds" min="0" max="600" step="1" value="${Math.round(r.sustainSeconds)}" aria-label="Seconds above before alerting">
        <span class="muted">s</span>
        <span class="alert-now faint"></span>
        <button type="button" class="btn sm ghost" data-remove aria-label="Remove this alert">Remove</button>
      </div>`).join('') || '<p class="faint">No alerts yet. Add one to get notified.</p>';
    updateRuleLive(state);
  }

  function updateRuleLive(state) {
    const firing = new Set(state.alerts?.firing ?? []);
    root.querySelectorAll('.alert-rule').forEach((row) => {
      const r = alerts.rules[Number(row.dataset.i)];
      const v = alertSensor(state, r.source)?.value;
      const el = row.querySelector('.alert-now');
      el.innerHTML = firing.has(r.id)
        ? `<span class="tag warn">Above now · ${Math.round(v)}°C</span>`
        : v != null ? `now ${Math.round(v)}°C` : 'no sensor';
    });
  }

  function alertsChanged() { $a('#al-save').disabled = same(alerts, alertsBase); }

  $a('#al-rules').addEventListener('input', (e) => {
    const row = e.target.closest('.alert-rule'); if (!row) return;
    const r = alerts.rules[Number(row.dataset.i)];
    const k = e.target.dataset.k;
    r[k] = k === 'enabled' ? e.target.checked : k === 'source' ? e.target.value : Number(e.target.value);
    if (k === 'source') updateRuleLive(lastSettingsState);
    alertsChanged();
  });
  $a('#al-rules').addEventListener('click', (e) => {
    if (!e.target.closest('[data-remove]')) return;
    alerts.rules.splice(Number(e.target.closest('.alert-rule').dataset.i), 1);
    renderRules(lastSettingsState); alertsChanged();
  });
  $a('#al-add').addEventListener('click', () => {
    if (alerts.rules.length >= 10) { toast('Up to 10 alerts are supported.', true); return; }
    alerts.rules.push({ id: Math.random().toString(36).slice(2, 10), enabled: true, source: 'cpu', threshold: 85, sustainSeconds: 10 });
    renderRules(lastSettingsState); alertsChanged();
  });
  for (const [sel, key] of [['#al-enabled', 'enabled'], ['#al-safety', 'notifyOnSafety'], ['#al-health', 'notifyOnHealth']]) {
    $a(sel).addEventListener('change', (e) => { alerts[key] = e.target.checked; alertsChanged(); });
  }
  $a('#al-save').addEventListener('click', () => run(async () => {
    await enableBrowserNotifications();
    const saved = await api('PUT', '/alerts', alerts);
    alerts = clone(saved); alertsBase = clone(saved);
    renderRules(lastSettingsState); alertsChanged();
  }, 'Alerts saved'));
  $a('#al-test').addEventListener('click', () => run(async () => {
    if (!(await enableBrowserNotifications())) throw new Error('Notifications are blocked for this page. Allow them in your browser settings.');
    await api('POST', '/alerts/test');
  }, 'Test sent. A notification should appear in a moment.'));
  $a('#al-where').textContent = !notificationsSupported()
    ? 'This browser can\'t show notifications. Use the Kelvin app instead.'
    : isDesktopApp()
      ? 'Notifications appear even while Kelvin is in the tray. They stop only if you quit the app from its tray icon.'
      : 'In a browser, notifications only appear while this tab is open. The Kelvin app shows them even from the tray.';
  let lastSettingsState = null;

  if (canUpdate()) {
    root.querySelector('#st-updates').hidden = false;
    root.querySelector('#st-check').addEventListener('click', async (e) => {
      e.target.disabled = true; e.target.textContent = 'Checking…';
      await checkForUpdate(true);
      e.target.disabled = false; e.target.textContent = 'Check now';
    });
    root.querySelector('#st-install').addEventListener('click', (e) => {
      e.target.disabled = true; e.target.textContent = 'Updating…'; installUpdate();
    });
  }

  let report = null;
  root.querySelector('#st-report').addEventListener('click', () => run(async () => {
    report = await api('GET', '/report');
    const box = root.querySelector('#st-report-preview');
    box.querySelector('.report').textContent = report.body;
    box.hidden = false;
    box.querySelector('.report').focus();
  }));
  root.querySelector('#st-report-copy').addEventListener('click', () => run(
    () => navigator.clipboard.writeText(report.body), 'Report copied'));
  root.querySelector('#st-report-open').addEventListener('click', () => run(async () => {
    // Too long for a link: the issue asks for a paste, so the report goes on the clipboard first.
    if (!report.bodyInUrl) { await navigator.clipboard.writeText(report.body); toast('Report copied. Paste it into the issue.'); }
    await openExternal(report.issueUrl);
  }));

  // Desktop-only settings, through the Tauri shell.
  const invoke = window.__TAURI__?.core?.invoke;
  if (invoke) {
    const box = root.querySelector('#st-autostart');
    root.querySelector('#st-app').hidden = false;
    invoke('plugin:autostart|is_enabled').then((on) => { box.checked = on; }).catch(() => {});
    box.addEventListener('change', () => run(
      () => invoke(box.checked ? 'plugin:autostart|enable' : 'plugin:autostart|disable'),
      box.checked ? 'Kelvin will start with Windows' : 'Kelvin won\'t start with Windows',
    ));
  }

  let loaded = false;
  root.querySelector('#st-save').addEventListener('click', () => run(() => api('PUT', '/safety', {
    criticalCpuTemp: Number(root.querySelector('#st-cpu').value),
    criticalGpuTemp: Number(root.querySelector('#st-gpu').value),
  }), 'Safety limits saved'));

  return {
    update(state) {
      if (!loaded) {
        root.querySelector('#st-cpu').value = state.safetySettings.criticalCpuTemp;
        root.querySelector('#st-gpu').value = state.safetySettings.criticalGpuTemp;
        loaded = true;
      }
      lastSettingsState = state;
      updateHa(state);
      if (!alerts && state.alertSettings) {
        alerts = clone(state.alertSettings); alertsBase = clone(state.alertSettings);
        $a('#al-enabled').checked = alerts.enabled;
        $a('#al-safety').checked = alerts.notifyOnSafety;
        $a('#al-health').checked = alerts.notifyOnHealth;
        renderRules(state);
      } else if (alerts) {
        updateRuleLive(state);
      }
      const recent = state.alerts?.recent ?? [];
      $a('#al-recent').innerHTML = recent.length
        ? `<span class="label" style="font-size:12.5px;color:var(--text-2);font-weight:500">Recent alerts</span>
           <ul class="alert-log">${recent.slice(0, 5).map((e) => `<li><span class="faint">${new Date(e.at).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' })}</span> ${esc(e.title)}</li>`).join('')}</ul>`
        : '';

      const s = state.system;
      root.querySelector('#st-version').textContent = `Version ${s.version}`;
      const u = getUpdate();
      root.querySelector('#st-install').hidden = !u;
      if (u) {
        root.querySelector('#st-update-title').textContent = `Kelvin ${u.version} is available`;
        root.querySelector('#st-update-detail').textContent = u.notes ? u.notes.split('\n')[0] : 'Updating takes under a minute.';
      }
      root.querySelector('#st-hw').innerHTML = `
        <tr><td>Fans and sensors</td><td>${esc(s.fanBackend)}</td><td class="muted">${s.simulated ? (s.elevated ? 'Started with --simulate' : 'Run as administrator for real hardware') : 'Connected'}</td></tr>
        <tr><td>Lighting</td><td>${esc(s.rgbBackend)}</td><td class="muted">${s.rgbConnected ? `${state.devices.length} devices` : esc(s.rgbStatus ?? 'Not connected')}</td></tr>`;
    },
  };
}

export const views = {
  overview: (root) => { const v = overview(root); bindRowLinks(root.firstElementChild); return v; },
  fans: fansView,
  lighting: lightingView,
  profiles: profilesView,
  health: healthView,
  advice: adviceView,
  settings: settingsView,
};
