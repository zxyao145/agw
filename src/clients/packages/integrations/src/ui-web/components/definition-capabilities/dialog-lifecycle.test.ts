import assert from "node:assert/strict";
import test from "node:test";

const MODULE_URL = new URL("./dialog-lifecycle.ts", import.meta.url);

async function importDialogLifecycleModule() {
  try {
    return await import(MODULE_URL.href);
  } catch (error) {
    assert.fail(`shared dialog lifecycle helper is missing or invalid: ${String(error)}`);
  }
}

test("applyDialogOpenChange ignores close and reopen requests while a mutation is pending", async () => {
  const { applyDialogOpenChange } = await importDialogLifecycleModule();
  const requestedStates: boolean[] = [];
  const setOpen = (open: boolean) => requestedStates.push(open);

  applyDialogOpenChange({ isPending: true, nextOpen: false, setOpen });
  applyDialogOpenChange({ isPending: true, nextOpen: true, setOpen });

  assert.deepEqual(requestedStates, []);
});

test("applyDialogOpenChange forwards open changes after the mutation settles", async () => {
  const { applyDialogOpenChange } = await importDialogLifecycleModule();
  const requestedStates: boolean[] = [];
  const setOpen = (open: boolean) => requestedStates.push(open);

  applyDialogOpenChange({ isPending: false, nextOpen: false, setOpen });
  applyDialogOpenChange({ isPending: false, nextOpen: true, setOpen });

  assert.deepEqual(requestedStates, [false, true]);
});
