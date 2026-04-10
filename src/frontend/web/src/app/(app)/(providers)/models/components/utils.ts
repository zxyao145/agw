export function parseIntOrNull(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed.length) return null;
  const n = Number(trimmed);
  if (!Number.isFinite(n)) return null;
  return Math.trunc(n);
}
