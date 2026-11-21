import { THERMAL } from './util.js';

// Fan curve editor. Drag points; double-click empty space to add one; double-click a point or
// press Delete to remove it; arrow keys nudge the focused point. The plot sits on the thermal scale.
const W = 640, H = 300;
const L = 44, R = 12, T = 14, B = 38;
const T_MIN = 20, T_MAX = 100;
const NS = 'http://www.w3.org/2000/svg';

const x = (t) => L + ((t - T_MIN) / (T_MAX - T_MIN)) * (W - L - R);
const y = (p) => T + (1 - p / 100) * (H - T - B);
const tAt = (px) => T_MIN + ((px - L) / (W - L - R)) * (T_MAX - T_MIN);
const pAt = (py) => (1 - (py - T) / (H - T - B)) * 100;
const clamp = (v, a, b) => Math.min(b, Math.max(a, v));

export function createCurveEditor(host, { points, onChange }) {
  let pts = points.map((p) => ({ ...p }));
  let live = null;

  const stops = THERMAL.map(([t, c]) => `<stop offset="${((t - T_MIN) / (T_MAX - T_MIN)).toFixed(3)}" stop-color="rgb(${c.join(' ')})"/>`).join('');
  host.innerHTML = `
    <div class="curve-wrap">
      <svg viewBox="0 0 ${W} ${H}" role="group" aria-label="Fan curve: fan speed by temperature">
        <defs>
          <linearGradient id="thermal-x" x1="${L}" x2="${W - R}" y1="0" y2="0" gradientUnits="userSpaceOnUse">${stops}</linearGradient>
          <linearGradient id="area-fill" x1="0" y1="0" x2="0" y2="1">
            <stop class="area-stop" offset="0" stop-opacity="0.28"/>
            <stop class="area-stop" offset="1" stop-opacity="0.02"/>
          </linearGradient>
        </defs>
        <rect x="${L}" y="${T}" width="${W - L - R}" height="${H - T - B}" fill="url(#thermal-x)" opacity="0.07"/>
        <g class="grid"></g>
        <rect x="${L}" y="${H - B + 6}" width="${W - L - R}" height="4" rx="2" fill="url(#thermal-x)"/>
        <path class="area" fill="url(#area-fill)"/>
        <path class="line" fill="none" stroke-width="2.25" stroke-linejoin="round"/>
        <g class="live"></g>
        <g class="points"></g>
      </svg>
    </div>
    <p class="curve-help">Drag a point to change it. Double-click empty space to add a point, or double-click a point to remove it.</p>`;

  const svg = host.querySelector('svg');
  const grid = svg.querySelector('.grid');
  const area = svg.querySelector('.area');
  const line = svg.querySelector('.line');
  const liveG = svg.querySelector('.live');
  const pointsG = svg.querySelector('.points');

  let g = '';
  for (let t = 30; t <= 90; t += 10) {
    g += `<line x1="${x(t)}" x2="${x(t)}" y1="${T}" y2="${H - B}"/>`;
    g += `<text x="${x(t)}" y="${H - 8}" font-size="11.5" text-anchor="middle">${t}°</text>`;
  }
  for (let p = 0; p <= 100; p += 25) {
    g += `<line x1="${L}" x2="${W - R}" y1="${y(p)}" y2="${y(p)}"/>`;
    g += `<text x="${L - 8}" y="${y(p) + 4}" font-size="11.5" text-anchor="end">${p}%</text>`;
  }
  grid.innerHTML = g;

  function toSvg(evt) {
    const pt = svg.createSVGPoint();
    pt.x = evt.clientX; pt.y = evt.clientY;
    return pt.matrixTransform(svg.getScreenCTM().inverse());
  }

  function sorted() { return [...pts].sort((a, b) => a.temp - b.temp); }

  function draw() {
    const s = sorted();
    const d = s.map((p, i) => `${i ? 'L' : 'M'}${x(p.temp).toFixed(1)},${y(p.percent).toFixed(1)}`).join(' ');
    const ext = `M${x(T_MIN)},${y(s[0].percent)} L${x(s[0].temp)},${y(s[0].percent)} ${d.slice(1)} L${x(T_MAX)},${y(s.at(-1).percent)}`;
    line.setAttribute('d', ext);
    area.setAttribute('d', `${ext} L${x(T_MAX)},${y(0)} L${x(T_MIN)},${y(0)} Z`);

    pointsG.innerHTML = '';
    pts.forEach((p, i) => {
      const c = document.createElementNS(NS, 'circle');
      c.setAttribute('cx', x(p.temp));
      c.setAttribute('cy', y(p.percent));
      c.setAttribute('r', 7);
          c.setAttribute('stroke-width', 2);
      c.setAttribute('class', 'point');
      c.setAttribute('tabindex', '0');
      c.setAttribute('role', 'slider');
      c.setAttribute('aria-label', `Point ${i + 1}: ${Math.round(p.percent)}% at ${Math.round(p.temp)}°C`);
      c.dataset.i = i;
      pointsG.appendChild(c);
    });
    drawLive();
  }

  function drawLive() {
    if (!live || live.temp == null) { liveG.innerHTML = ''; return; }
    const lx = x(clamp(live.temp, T_MIN, T_MAX));
    const ly = y(clamp(live.percent ?? 0, 0, 100));
    const label = `${Math.round(live.temp)}°C, ${Math.round(live.percent ?? 0)}%`;
    const anchorEnd = lx > W - 140;
    liveG.innerHTML = `
      <line x1="${lx}" x2="${lx}" y1="${T}" y2="${H - B}" stroke-opacity="0.35" stroke-dasharray="3 4"/>
      <circle cx="${lx}" cy="${ly}" r="5"/>
      <text x="${lx + (anchorEnd ? -10 : 10)}" y="${Math.max(ly - 10, T + 12)}" font-size="12" font-weight="600" text-anchor="${anchorEnd ? 'end' : 'start'}">${label}</text>`;
  }

  function limits(i) {
    // A point can't pass its neighbours in temperature.
    const order = sorted();
    const idx = order.indexOf(pts[i]);
    const lo = idx > 0 ? order[idx - 1].temp + 1 : T_MIN;
    const hi = idx < order.length - 1 ? order[idx + 1].temp - 1 : T_MAX;
    return [lo, hi];
  }

  function emit() { onChange(sorted().map((p) => ({ temp: Math.round(p.temp), percent: Math.round(p.percent) }))); }

  let drag = null;
  svg.addEventListener('pointerdown', (e) => {
    const c = e.target.closest('.point');
    if (!c) return;
    drag = Number(c.dataset.i);
    svg.setPointerCapture(e.pointerId);
    e.preventDefault();
  });
  svg.addEventListener('pointermove', (e) => {
    if (drag == null) return;
    const p = toSvg(e);
    const [lo, hi] = limits(drag);
    pts[drag].temp = Math.round(clamp(tAt(p.x), lo, hi));
    pts[drag].percent = Math.round(clamp(pAt(p.y), 0, 100));
    draw();
    pointsG.children[drag]?.focus({ preventScroll: true });
  });
  const end = () => { if (drag != null) { drag = null; emit(); } };
  svg.addEventListener('pointerup', end);
  svg.addEventListener('pointercancel', end);

  svg.addEventListener('dblclick', (e) => {
    const c = e.target.closest('.point');
    if (c) {
      if (pts.length > 2) { pts.splice(Number(c.dataset.i), 1); draw(); emit(); }
      return;
    }
    const p = toSvg(e);
    const t = Math.round(clamp(tAt(p.x), T_MIN, T_MAX));
    if (pts.some((q) => Math.abs(q.temp - t) < 2)) return;
    pts.push({ temp: t, percent: Math.round(clamp(pAt(p.y), 0, 100)) });
    draw(); emit();
  });

  svg.addEventListener('keydown', (e) => {
    const c = e.target.closest('.point');
    if (!c) return;
    const i = Number(c.dataset.i);
    const step = e.shiftKey ? 5 : 1;
    const [lo, hi] = limits(i);
    if (e.key === 'ArrowUp') pts[i].percent = clamp(pts[i].percent + step, 0, 100);
    else if (e.key === 'ArrowDown') pts[i].percent = clamp(pts[i].percent - step, 0, 100);
    else if (e.key === 'ArrowRight') pts[i].temp = clamp(pts[i].temp + step, lo, hi);
    else if (e.key === 'ArrowLeft') pts[i].temp = clamp(pts[i].temp - step, lo, hi);
    else if ((e.key === 'Delete' || e.key === 'Backspace') && pts.length > 2) { pts.splice(i, 1); draw(); emit(); return; }
    else return;
    e.preventDefault();
    draw();
    pointsG.children[i]?.focus({ preventScroll: true });
    emit();
  });

  draw();

  return {
    setPoints(next) { pts = next.map((p) => ({ ...p })); draw(); },
    setLive(temp, percent) { live = { temp, percent }; drawLive(); },
  };
}
