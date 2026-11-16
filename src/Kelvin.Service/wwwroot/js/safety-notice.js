// When to show the floating safety-override notice. Pure: state and time in, decision out.
//
// - An episode starts with an override and ends after EPISODE_GAP with none. Short dips belong to
//   the same episode, so a CPU hovering at its limit (common on laptops) is one episode, not many.
// - The notice first appears once the episode has lasted SHOW_AFTER, and only while an override is
//   active (a short spike that's already over never surfaces).
// - Once shown it stays for the rest of the episode ("cooling down" during dips) and for at least
//   MIN_VISIBLE, so it never flickers or comes back again and again.
// - Dismissed: hidden for the rest of the episode.
export const SHOW_AFTER = 60_000;
export const MIN_VISIBLE = 30_000;
export const EPISODE_GAP = 60_000;

export const initial = () => ({ since: null, offSince: null, shownAt: null, dismissed: false });

/** Returns the next state and what to show: 'override', 'cooled' or null. */
export function step(prev, override, now) {
  const s = { ...prev };
  if (override) {
    if (s.since === null) Object.assign(s, initial(), { since: now });
    s.offSince = null;
  } else if (s.since !== null) {
    s.offSince ??= now;
    const ended = now - s.offSince >= EPISODE_GAP;
    const shownLongEnough = s.shownAt === null || now - s.shownAt >= MIN_VISIBLE;
    if (ended && shownLongEnough) return { state: initial(), show: null };
  }

  if (s.since === null || s.dismissed) return { state: s, show: null };
  if (s.shownAt === null) {
    if (!override || now - s.since < SHOW_AFTER) return { state: s, show: null };
    s.shownAt = now;
  }
  return { state: s, show: override ? 'override' : 'cooled' };
}

export const dismiss = (prev) => ({ ...prev, dismissed: true });
