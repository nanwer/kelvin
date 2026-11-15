import { api, toast, go } from './app.js';
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
  if (state.safety.override) {
    out.push(`<div class="banner crit" role="alert">${icon('shield')}<div class="grow"><strong>Safety override</strong><p>${esc(state.safety.reason)}</p></div></div>`);
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
    if (!fan.controllable) { el.innerHTML = `<div class="empty"><strong>${esc(fan.name)} is read only</strong><span>Kelvin can see its speed but this header doesn't accept speed changes.</span></div>`; return; }

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

function lightingView(root) {
  root.innerHTML = `<div class="page">
    <header class="page-head"><div><h1>Lighting</h1><p>One effect across every device. Changes apply as you make them.</p></div></header>
    <div class="stack" id="lx-banners"></div>
    <div id="lx-body"></div>
  </div>`;

  let draft = null;
  let editingUntil = 0;
  let lastState = null;
  let structureKey = '';
  let bannerKey = '';

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
        <div id="lx-devices">${state.devices.map((dev) => `
          <div class="device ${dev.included ? '' : 'off'}" data-id="${esc(dev.id)}">
            <div><strong>${esc(dev.name)}</strong><span class="faint">${esc(dev.vendor)} · ${esc(dev.kind)} · ${dev.ledCount} LEDs</span></div>
            ${ledStrip(state.preview[dev.id], dev.ledCount)}
            <label class="switch" title="${dev.included ? 'Kelvin controls this device' : 'Kelvin leaves this device alone'}">
              <input type="checkbox" ${dev.included ? 'checked' : ''} aria-label="Kelvin controls ${esc(dev.name)}"><span></span></label>
          </div>`).join('') || '<div class="empty"><strong>No lighting devices found</strong><span>OpenRGB is running but didn\'t report any devices. Try Rescan Devices in OpenRGB.</span></div>'}
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
    body.querySelector('#lx-devices').addEventListener('change', (e) => {
      const row = e.target.closest('.device'); if (!row) return;
      const id = row.dataset.id;
      change((x) => {
        x.excludedDeviceIds = x.excludedDeviceIds.filter((v) => v !== id);
        if (!e.target.checked) x.excludedDeviceIds.push(id);
      });
      row.classList.toggle('off', !e.target.checked);
    });
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
    <section class="panel">
      <div class="panel-head"><h2>Hardware</h2></div>
      <table><tbody id="st-hw"></tbody></table>
    </section>
  </div>`;

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
      const s = state.system;
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
  advice: adviceView,
  settings: settingsView,
};
