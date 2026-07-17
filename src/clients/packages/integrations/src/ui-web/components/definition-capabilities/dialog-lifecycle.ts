interface ApplyDialogOpenChangeOptions {
  isPending: boolean;
  nextOpen: boolean;
  setOpen: (open: boolean) => void;
}

export function applyDialogOpenChange({
  isPending,
  nextOpen,
  setOpen,
}: ApplyDialogOpenChangeOptions): void {
  if (isPending) {
    return;
  }

  setOpen(nextOpen);
}
