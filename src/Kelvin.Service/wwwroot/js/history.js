// History: every temperature, fan and load over the last hour or day, as line charts that share
// one time axis and one crosshair. The table under each chart is its legend, its on/off switches
// and its plain-number view in one.
import { api, toast } from './app.js';
import { esc, sensorLabel } from './util.js';

// Eight line colors, checked as a set for color-blind separation and contrast on the panel
// background. A line keeps its color for as long as it is switched on.
const PALETTE = ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#0ca30c', '#9085e9', '#e66767'];
const MAX_LINES = PALETTE.length;

const RANGES = {
  hour: { seconds: 3600, label: 'Last hour', refresh: 5000, tick: 900 },
  day: { seconds: 86400, label: 'Last day', refresh: 30000, tick: 3 * 3600 },
};

const CHARTS = [
  { key: 'temperature', title: 'Temperatures', what: 'temperature' },
  { key: 'fans', title: 'Fans', what: 'fan' },
  { key: 'load', title: 'Load', what: 'load' },
];

const FAN_UNITS = { rpm: { label: 'Speed (rpm)', unit: ' rpm' }, duty: { label: 'Duty (%)', unit: '%' } };

function loadPrefs() {
  try { return JSON.parse(localStorage.getItem('kelvin.history') || '{}') ?? {}; } catch { return {}; }
}
function savePrefs(p) {
  try { localStorage.setItem('kelvin.history', JSON.stringify(p)); } catch { /* storage off: defaults next time */ }
}

const clock = (t, withSeconds) => new Date(t * 1000).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', ...(withSeconds ? { second: '2-digit' } : {}) });

function niceStep(span, count) {
  const raw = span / count;
  const pow = 10 ** Math.floor(Math.log10(raw));
  const n = raw / pow;
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * pow;
}

// Index of the reading closest in time to t.
function nearest(times, t) {
  let lo = 0, hi = times.length - 1;
  while (hi - lo > 1) { const mid = (lo + hi) >> 1; if (times[mid] < t) lo = mid; else hi = mid; }
  return Math.abs(times[lo] - t) <= Math.abs(times[hi] - t) ? lo : hi;
}

function stats(values) {
  let min = Infinity, max = -Infinity, sum = 0, n = 0, last = null;
  for (const v of values) {
    if (v == null) continue;
    if (v < min) min = v;
    if (v > max) max = v;
    sum += v; n++; last = v;
  }
  return n ? { min, max, avg: sum / n, last } : null;
}

// The things one chart can show, each with a label and its readings.
function entitiesFor(chart, data, fanUnit) {
  if (chart.what === 'fan') {
    return data.series.filter((s) => s.kind === fanUnit).map((s) => ({
      id: s.id.split('|')[0], label: s.name, values: s.values, primary: s.values.some((v) => v > 0),
    }));
  }
  return data.series.filter((s) => s.kind === chart.what)
    .map((s) => ({
      id: s.id, values: s.values, primary: s.primary || chart.what === 'load',
      label: chart.what === 'load' ? (s.role === 'cpu' ? 'CPU' : s.role === 'gpu' ? 'Graphics card' : s.name) : sensorLabel({ ...s, hardware: s.group }),
      order: ({ cpu: 0, gpu: 1, board: 2 })[s.role] ?? 3,
    }))
    .sort((a, b) => (b.primary - a.primary) || (a.order - b.order));
}

export function historyView(root) {
  const prefs = { range: 'hour', fanUnit: 'rpm', picked: {}, ...loadPrefs() };
  if (!RANGES[prefs.range]) prefs.range = 'hour';
  if (!FAN_UNITS[prefs.fanUnit]) prefs.fanUnit = 'rpm';
  prefs.picked ??= {};

  root.innerHTML = `<div class="page">
    <header class="page-head"><div><h1>History</h1><p>Every temperature and fan Kelvin can see, over the last hour or the last day. Point at a chart to read all three at that moment.</p></div>
      <div class="actions"><div class="segmented" role="group" aria-label="Time range" id="hi-range">
        ${Object.entries(RANGES).map(([k, r]) => `<button type="button" data-v="${k}" aria-pressed="${k === prefs.range}">${r.label}</button>`).join('')}
      </div></div></header>
    <div id="hi-body" class="stack"></div>
  </div>`;

  const body = root.querySelector('#hi-body');
  body.innerHTML = CHARTS.map((c) => `<section class="panel hist" data-chart="${c.key}">
      <div class="panel-head"><h2>${c.title}</h2>
        ${c.what === 'fan' ? `<div class="segmented" role="group" aria-label="Fan measure" data-fan-unit>
          ${Object.entries(FAN_UNITS).map(([k, u]) => `<button type="button" data-v="${k}" aria-pressed="${k === prefs.fanUnit}">${u.label}</button>`).join('')}</div>` : '<span class="meta"></span>'}
      </div>
      <div class="hist-plot"><div class="hist-svg"></div><div class="hist-tip" hidden></div></div>
      <div class="hist-table"></div>
    </section>`).join('');

  let data = null;
  let timer = null;
  let hoverT = null;
  let destroyed = false;
  const drawn = {}; // per chart: what the last draw used, so the crosshair can be placed without redrawing

  async function load() {
    try {
      const next = await api('GET', `/history?range=${prefs.range}`);
      if (destroyed || next.range !== prefs.range) return;
      data = next;
      render();
    } catch { /* the service is restarting; the next refresh picks it up */ }
  }

  function schedule() {
    clearInterval(timer);
    timer = setInterval(load, RANGES[prefs.range].refresh);
  }

  // Which lines are on, and the color slot each one holds. Decided once from sensible defaults,
  // then only ever changed by the person: a line that stays on never changes color.
  function pickedFor(chart, entities) {
    let picked = prefs.picked[chart.key];
    if (!picked) {
      picked = {};
      const first = entities.filter((e) => e.primary).slice(0, chart.what === 'temperature' ? 4 : MAX_LINES);
      (first.length ? first : entities.slice(0, 3)).forEach((e, i) => { picked[e.id] = i; });
      if (entities.length) prefs.picked[chart.key] = picked;
    }
    return picked;
  }

  function toggle(chart, id, on, entities) {
    const picked = pickedFor(chart, entities);
    if (!on) { delete picked[id]; }
    else {
      const present = new Set(entities.map((e) => e.id));
      // A sensor that no longer exists gives its color back.
      for (const k of Object.keys(picked)) if (!present.has(k)) delete picked[k];
      const used = new Set(Object.values(picked));
      const slot = PALETTE.findIndex((_, i) => !used.has(i));
      if (slot < 0) { toast(`A chart shows up to ${MAX_LINES} lines. Turn one off first.`, true); return false; }
      picked[id] = slot;
    }
    savePrefs(prefs);
    return true;
  }

  function render() {
    if (!data) return;
    const range = RANGES[prefs.range];
    const now = Math.floor(Date.now() / 1000);
    const t1 = Math.max(now, data.times.at(-1) ?? 0), t0 = t1 - range.seconds;
    for (const chart of CHARTS) {
      const panel = body.querySelector(`[data-chart="${chart.key}"]`);
      const entities = entitiesFor(chart, data, prefs.fanUnit);
      panel.hidden = entities.length === 0 && data.times.length > 0;
      const picked = pickedFor(chart, entities);
      const lines = entities.filter((e) => picked[e.id] != null).map((e) => ({ ...e, color: PALETTE[picked[e.id]] }));
      const unit = chart.what === 'temperature' ? '°C' : chart.what === 'fan' ? FAN_UNITS[prefs.fanUnit].unit : '%';
      const fixed = chart.what === 'load' || (chart.what === 'fan' && prefs.fanUnit === 'duty') ? [0, 100] : null;
      drawn[chart.key] = drawChart(panel.querySelector('.hist-svg'), {
        times: data.times, step: data.stepSeconds, lines, t0, t1, tick: range.tick, unit, fixed,
        fromZero: chart.what === 'fan', label: `${chart.title}, ${range.label.toLowerCase()}`,
      });
      renderTable(panel.querySelector('.hist-table'), chart, entities, picked, unit);
    }
    placeHover();
  }

  function renderTable(host, chart, entities, picked, unit) {
    if (!entities.length) { host.innerHTML = ''; return; }
    const fmt = (v) => (v == null ? '–' : `${Math.round(v).toLocaleString('en-US')}${unit === '°C' ? '°' : unit}`);
    const key = JSON.stringify([entities.map((e) => [e.id, e.label]), picked, unit]);
    if (host.dataset.key !== key) {
      host.dataset.key = key;
      host.innerHTML = `<table><thead><tr><th>Show</th><th class="num">Now</th><th class="num">Lowest</th><th class="num">Average</th><th class="num">Highest</th></tr></thead><tbody>
        ${entities.map((e) => `<tr data-id="${esc(e.id)}">
          <td><label class="hist-key"><input type="checkbox" ${picked[e.id] != null ? 'checked' : ''}>
            <i style="${picked[e.id] != null ? `background:${PALETTE[picked[e.id]]}` : ''}"></i><span>${esc(e.label)}</span></label></td>
          <td class="num"></td><td class="num"></td><td class="num"></td><td class="num"></td></tr>`).join('')}
      </tbody></table>`;
    }
    for (const e of entities) {
      const cells = host.querySelector(`tr[data-id="${CSS.escape(e.id)}"]`)?.children;
      if (!cells) continue;
      const s = stats(e.values);
      [s?.last, s?.min, s?.avg, s?.max].forEach((v, i) => { cells[i + 1].textContent = fmt(v); });
    }
  }

  // ---- crosshair: one moment, read across all three charts
  function placeHover() {
    for (const chart of CHARTS) {
      const panel = body.querySelector(`[data-chart="${chart.key}"]`);
      const d = drawn[chart.key];
      const tip = panel.querySelector('.hist-tip');
      const layer = panel.querySelector('.hist-hover');
      if (!d || !layer) { tip.hidden = true; continue; }
      const i = hoverT == null || !d.times.length ? -1 : nearest(d.times, hoverT);
      if (i < 0 || Math.abs(d.times[i] - hoverT) > d.step * 3) { layer.innerHTML = ''; tip.hidden = true; continue; }
      const x = d.x(d.times[i]);
      const dots = d.lines.filter((l) => l.values[i] != null);
      layer.innerHTML = `<line x1="${x}" x2="${x}" y1="${d.top}" y2="${d.bottom}" class="hair"/>`
        + dots.map((l) => `<circle cx="${x}" cy="${d.y(l.values[i])}" r="4.5" fill="${l.color}" class="dot"/>`).join('');
      tip.replaceChildren();
      const when = document.createElement('div');
      when.className = 'when';
      when.textContent = clock(d.times[i], d.step < 60);
      tip.append(when);
      for (const l of [...dots].sort((a, b) => b.values[i] - a.values[i])) {
        const row = document.createElement('div');
        row.className = 'row';
        const key = document.createElement('i');
        key.style.background = l.color;
        const value = document.createElement('strong');
        const v = l.values[i];
        value.textContent = d.unit === '°C' ? `${v.toFixed(1)}°C` : `${Math.round(v).toLocaleString('en-US')}${d.unit}`;
        const name = document.createElement('span');
        name.textContent = l.label;
        row.append(key, value, name);
        tip.append(row);
      }
      if (!dots.length) { tip.hidden = true; continue; }
      tip.hidden = false;
      // Sit beside the crosshair, on whichever side has room.
      const left = x / d.width > 0.6;
      tip.style.left = left ? '' : `${x + 14}px`;
      tip.style.right = left ? `${d.width - x + 14}px` : '';
    }
  }

  body.addEventListener('pointermove', (e) => {
    const plot = e.target.closest('.hist-plot');
    const key = plot?.closest('[data-chart]')?.dataset.chart;
    const d = key && drawn[key];
    if (!d) return;
    const px = e.clientX - plot.getBoundingClientRect().left;
    const next = px < d.left || px > d.right ? null : d.t(px);
    if (next === hoverT) return;
    hoverT = next;
    placeHover();
  });
  body.addEventListener('pointerleave', () => { hoverT = null; placeHover(); });

  body.addEventListener('change', (e) => {
    if (!e.target.matches('.hist-key input') || !data) return;
    const key = e.target.closest('[data-chart]').dataset.chart;
    const chart = CHARTS.find((c) => c.key === key);
    const id = e.target.closest('tr').dataset.id;
    if (!toggle(chart, id, e.target.checked, entitiesFor(chart, data, prefs.fanUnit))) e.target.checked = false;
    render();
  });

  body.addEventListener('click', (e) => {
    const b = e.target.closest('[data-fan-unit] button');
    if (!b) return;
    prefs.fanUnit = b.dataset.v;
    savePrefs(prefs);
    b.parentElement.querySelectorAll('button').forEach((x) => x.setAttribute('aria-pressed', x === b));
    render();
  });

  root.querySelector('#hi-range').addEventListener('click', (e) => {
    const b = e.target.closest('button');
    if (!b || b.dataset.v === prefs.range) return;
    prefs.range = b.dataset.v;
    savePrefs(prefs);
    b.parentElement.querySelectorAll('button').forEach((x) => x.setAttribute('aria-pressed', x === b));
    data = null; hoverT = null;
    load(); schedule();
  });

  // Redraw at the new width only: a redraw changes the page's height, which must not loop back here.
  let lastWidth = 0;
  const resize = new ResizeObserver(() => {
    const w = body.clientWidth;
    if (w !== lastWidth) { lastWidth = w; render(); }
  });
  resize.observe(body);
  load();
  schedule();

  return {
    update() {},
    destroy() { destroyed = true; clearInterval(timer); resize.disconnect(); },
  };
}

// Draws one chart and returns what the crosshair needs to find its way around it.
function drawChart(host, { times, step, lines, t0, t1, tick, unit, fixed, fromZero, label }) {
  const W = Math.max(host.clientWidth, 320), H = 220, L = 52, R = 14, T = 12, B = 26;
  const visible = lines.flatMap((l) => l.values.filter((v, i) => v != null && times[i] >= t0));
  if (times.length < 2 || !visible.length) {
    host.innerHTML = `<div class="empty hist-empty"><strong>${lines.length ? 'Nothing recorded yet' : 'No lines switched on'}</strong>
      <span>${lines.length ? 'Kelvin has only just started keeping history. The chart fills in as it runs.' : 'Tick something in the table below to chart it.'}</span></div>`;
    return null;
  }

  let lo, hi;
  if (fixed) [lo, hi] = fixed;
  else {
    lo = fromZero ? 0 : Math.min(...visible); hi = Math.max(...visible);
    if (hi - lo < 8) { const mid = (hi + lo) / 2; lo = fromZero ? 0 : mid - 4; hi = Math.max(hi, mid + 4); }
  }
  const yStep = niceStep(hi - lo, 4);
  if (!fixed) { lo = Math.floor(lo / yStep) * yStep; hi = Math.ceil(hi / yStep) * yStep; }

  const x = (t) => L + ((t - t0) / (t1 - t0)) * (W - L - R);
  const y = (v) => T + (1 - (v - lo) / (hi - lo)) * (H - T - B);

  let grid = '';
  for (let v = lo; v <= hi + yStep / 2; v += yStep) {
    grid += `<line x1="${L}" x2="${W - R}" y1="${y(v).toFixed(1)}" y2="${y(v).toFixed(1)}" class="grid"/>
      <text x="${L - 8}" y="${(y(v) + 4).toFixed(1)}" text-anchor="end">${Math.round(v).toLocaleString('en-US')}${unit === '°C' ? '°' : unit === '%' ? '%' : ''}</text>`;
  }
  // Time marks on round clock times, in the viewer's own time zone.
  const first = new Date(t0 * 1000);
  first.setSeconds(0, 0);
  if (tick >= 3600) { first.setMinutes(0); first.setHours(Math.ceil(first.getHours() / (tick / 3600) + 0.001) * (tick / 3600)); }
  else first.setMinutes(Math.ceil(first.getMinutes() / (tick / 60) + 0.001) * (tick / 60));
  for (let t = first.getTime() / 1000; t <= t1; t += tick) {
    if (x(t) < L + 18 || x(t) > W - R - 18) continue;
    grid += `<line x1="${x(t).toFixed(1)}" x2="${x(t).toFixed(1)}" y1="${H - B}" y2="${H - B + 4}" class="grid"/>
      <text x="${x(t).toFixed(1)}" y="${H - 6}" text-anchor="middle">${clock(t)}</text>`;
  }

  // A line breaks wherever readings are missing, so time Kelvin wasn't running stays empty.
  const paths = lines.map((l) => {
    let d = '', pen = false;
    for (let i = 0; i < times.length; i++) {
      const v = l.values[i];
      if (v == null || times[i] < t0) { pen = false; continue; }
      if (pen && times[i] - times[i - 1] > step * 3) pen = false;
      d += `${pen ? 'L' : 'M'}${x(times[i]).toFixed(1)},${y(v).toFixed(1)}`;
      pen = true;
    }
    return `<path d="${d}" stroke="${l.color}" class="line"/>`;
  }).join('');

  host.innerHTML = `<svg viewBox="0 0 ${W} ${H}" width="${W}" height="${H}" role="img" aria-label="${esc(label)}. The table below has the numbers.">
    ${grid}${paths}<g class="hist-hover"></g></svg>`;
  return {
    times, step, lines, unit, x, y, width: W, left: L, right: W - R, top: T, bottom: H - B,
    t: (px) => t0 + ((px - L) / (W - L - R)) * (t1 - t0),
  };
}
