export function matchesNodeLibrarySearch(query: string, values: readonly string[]): boolean {
  const normalizedQuery = query.trim().toLocaleLowerCase();
  return values.some((value) => value.toLocaleLowerCase().includes(normalizedQuery));
}
