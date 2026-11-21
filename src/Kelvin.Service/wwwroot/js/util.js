// Same stops as LightingEngine.ThermalScale on the server, so colors mean the same thing everywhere.
export const THERMAL = [
  [35, [0x3d, 0x7b, 0xff]],
  [50, [0x9e, 0xc8, 0xff]],
  [62, [0xff, 0xe2, 0xb8]],
  [72, [0xff, 0x9a, 0x3d]],
  [85, [0xff, 0x2e, 0x1f]],
];

export function thermal(temp) {
  if (temp <= THERMAL[0][0]) return rgb(THERMAL[0][1]);
  for (let i = 1; i < THERMAL.length; i++) {
    const [t0, c0] = THERMAL[i - 1];
    const [t1, c1] = THERMAL[i];
    if (temp <= t1) {
      const k = (temp - t0) / (t1 - t0);
      return rgb(c0.map((v, j) => Math.round(v + (c1[j] - v) * k)));
    }
  }
  return rgb(THERMAL.at(-1)[1]);
}

const rgb = ([r, g, b]) => `rgb(${r} ${g} ${b})`;

export const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

export const fmtTemp = (v) => (v == null || !isFinite(v) ? '–' : Math.round(v).toString());
export const fmtRpm = (v) => (v == null ? '–' : Math.round(v).toLocaleString('en-US'));
export const fmtPct = (v) => (v == null ? '–' : `${Math.round(v)}%`);

export const clone = (o) => JSON.parse(JSON.stringify(o));
export const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

export function debounce(fn, ms) {
  let t;
  return (...args) => {
    clearTimeout(t);
    t = setTimeout(() => fn(...args), ms);
  };
}

export const temperatureSensors = (state) =>
  state.sensors
    .filter((s) => s.kind === 'temperature')
    .sort((a, b) => (b.role === 'virtual') - (a.role === 'virtual') || (b.primary - a.primary));

export function sensorLabel(s) {
  if (!s) return 'Unknown sensor';
  if (s.role === 'virtual') return s.name;
  const hw = s.role === 'cpu' ? 'CPU' : s.role === 'gpu' ? 'GPU' : s.role === 'board' ? 'Motherboard' : s.hardware;
  return s.name.toLowerCase().startsWith(hw.toLowerCase()) ? s.name : `${hw}, ${s.name}`;
}

export function describeControl(fan, state) {
  const s = fan.settings;
  if (!fan.controllable) return 'Read only';
  if (!s || s.mode === 'bios') return 'BIOS';
  if (s.mode === 'fixed') return `Fixed ${Math.round(s.fixedPercent)}%`;
  const src = state.sensors.find((x) => x.id === s.sourceSensorId);
  const preset = s.preset && s.preset !== 'custom' ? s.preset[0].toUpperCase() + s.preset.slice(1) : 'Custom';
  const follows = !src ? 'no sensor' : src.role === 'virtual' ? 'hottest' : src.role === 'cpu' ? 'CPU' : src.role === 'gpu' ? 'GPU' : src.name;
  return `${preset}, follows ${follows}`;
}
