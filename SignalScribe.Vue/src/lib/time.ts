// UTC everywhere server-side; the browser owns all local-time conversion (CLAUDE.md).

/** Renders a server UTC timestamp in the viewer's local time. */
export function formatLocal(utc: string | null): string {
  if (!utc) return "—";
  const iso = utc.endsWith("Z") ? utc : `${utc}Z`;
  return new Date(iso).toLocaleString();
}

export function formatFrequency(hz: number): string {
  return `${(hz / 1_000_000).toFixed(4)} MHz`;
}

export const DAY_NAMES = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

/**
 * Weekly schedule conversion. Uses the current week's offset, so a UTC-fixed schedule
 * displays shifted by an hour across DST — accepted trade-off (see plan.md).
 */
export function utcWeeklyToLocal(dayUtc: number, timeUtc: string): { day: number; time: string } {
  const [h, m] = timeUtc.split(":").map(Number);
  const now = new Date();
  const d = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate(), h, m));
  d.setUTCDate(d.getUTCDate() + ((dayUtc - d.getUTCDay() + 7) % 7));
  return { day: d.getDay(), time: `${pad(d.getHours())}:${pad(d.getMinutes())}` };
}

export function localWeeklyToUtc(dayLocal: number, timeLocal: string): { day: number; time: string } {
  const [h, m] = timeLocal.split(":").map(Number);
  const now = new Date();
  const d = new Date(now.getFullYear(), now.getMonth(), now.getDate(), h, m);
  d.setDate(d.getDate() + ((dayLocal - d.getDay() + 7) % 7));
  return { day: d.getUTCDay(), time: `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:00` };
}

function pad(n: number): string {
  return n.toString().padStart(2, "0");
}
