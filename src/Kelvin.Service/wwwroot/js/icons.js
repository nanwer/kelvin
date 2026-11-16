// Authored line icons: 20px grid, 1.6 stroke, round joins. One family, one weight.
const paths = {
  overview: '<rect x="3" y="3" width="6" height="6" rx="1.5"/><rect x="11" y="3" width="6" height="6" rx="1.5"/><rect x="3" y="11" width="6" height="6" rx="1.5"/><rect x="11" y="11" width="6" height="6" rx="1.5"/>',
  fans: '<circle cx="10" cy="10" r="1.6"/><path d="M10 8.4C9.2 5.6 9.6 3 11.6 3c2.2 0 2.4 3.2-.4 5.6"/><path d="M11.4 10.8c2.8.8 4.6 2.8 3.6 4.5-1.1 1.9-3.9.3-4.6-3.3"/><path d="M8.6 10.8c-2 2.1-4.6 2.7-5.6 1-1.1-1.9 1.6-3.6 5.1-2.5"/>',
  lighting: '<path d="M7.5 14.5h5M8 17h4"/><path d="M10 3a5 5 0 0 0-3 9c.6.5 1 1.2 1 2h4c0-.8.4-1.5 1-2a5 5 0 0 0-3-9Z"/>',
  profiles: '<path d="m10 3 7 3.5-7 3.5-7-3.5Z"/><path d="m3 10 7 3.5 7-3.5"/><path d="m3 13.5 7 3.5 7-3.5"/>',
  health: '<path d="M2.5 10.5h3.2l1.8-4 3 8 2-5.5 1.3 1.5h3.7"/>',
  advice: '<path d="M10 2.8 11.6 7a1.5 1.5 0 0 0 .9.9l4.2 1.6-4.2 1.6a1.5 1.5 0 0 0-.9.9L10 16.2 8.4 12a1.5 1.5 0 0 0-.9-.9L3.3 9.5l4.2-1.6a1.5 1.5 0 0 0 .9-.9Z"/>',
  settings: '<path d="M4 6h7M15 6h1M4 14h1M9 14h7"/><circle cx="13" cy="6" r="2"/><circle cx="7" cy="14" r="2"/>',
  alert: '<path d="M10 3.2 17.4 16H2.6Z"/><path d="M10 8.2v3.6M10 13.9v.1"/>',
  info: '<circle cx="10" cy="10" r="7"/><path d="M10 9v4.4M10 6.6v.1"/>',
  check: '<path d="m4.5 10.5 3.5 3.5 7.5-8"/>',
  close: '<path d="m5.5 5.5 9 9M14.5 5.5l-9 9"/>',
  plus: '<path d="M10 4.5v11M4.5 10h11"/>',
  shield: '<path d="M10 2.8 16 5v4.6c0 3.6-2.5 6.3-6 7.6-3.5-1.3-6-4-6-7.6V5Z"/>',
  gauge: '<path d="M3.5 13.5a6.5 6.5 0 1 1 13 0"/><path d="m10 13.5 3-4"/>',
  plug: '<path d="M7 3v4M13 3v4M5 7h10v2a5 5 0 0 1-10 0Z"/><path d="M10 14v3"/>',
};

export function icon(name, size = 18) {
  return `<svg width="${size}" height="${size}" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${paths[name] ?? ''}</svg>`;
}
