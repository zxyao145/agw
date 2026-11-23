export function isEditableKeyboardTarget(target: EventTarget | null): boolean {
  return (
    typeof HTMLElement !== "undefined" &&
    target instanceof HTMLElement &&
    (target.isContentEditable ||
      Boolean(target.closest("input, textarea, select, [contenteditable]")))
  );
}

export function isRadixPopoverTarget(target: EventTarget | null): boolean {
  return (
    typeof Element !== "undefined" &&
    target instanceof Element &&
    Boolean(target.closest("[data-radix-popper-content-wrapper]"))
  );
}

export function isAgentflowEditorKeyboardTarget(target: EventTarget | null): boolean {
  if (typeof Element === "undefined" || !(target instanceof Element)) return false;
  const builder = document.getElementById("agentflow-builder");
  if (!builder) return false;
  return Boolean(
    target.closest("#agentflow-builder") || target.closest('[role="dialog"]')?.contains(builder),
  );
}
