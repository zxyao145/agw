export async function copyCurrentUrlToClipboard(
  currentUrl: string,
  writeText: (value: string) => Promise<void>,
): Promise<void> {
  await writeText(currentUrl);
}
