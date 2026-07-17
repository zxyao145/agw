export const DEFAULT_PROJECT_ID = "11111111-1111-1111-1111-000000000001";

export function normalizeProjectTabs(
  storedIds: string[],
  availableIds: string[],
  openedId?: string | null,
): string[] {
  const available = new Set(availableIds);
  const tabs = [DEFAULT_PROJECT_ID];
  for (const id of storedIds) {
    if (id !== DEFAULT_PROJECT_ID && available.has(id) && !tabs.includes(id)) tabs.push(id);
  }
  if (openedId && available.has(openedId) && !tabs.includes(openedId)) tabs.push(openedId);
  return tabs;
}
