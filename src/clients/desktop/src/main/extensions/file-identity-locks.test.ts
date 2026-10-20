import assert from "node:assert/strict";
import test from "node:test";

import { FileIdentityLocks } from "./file-identity-locks";

function gate(): { promise: Promise<void>; open(): void } {
  let open!: () => void;
  const promise = new Promise<void>((resolve) => (open = resolve));
  return { promise, open };
}

test("writes to one file run one at a time in arrival order while other files run in parallel", async () => {
  const locks = new FileIdentityLocks();
  const events: string[] = [];
  const first = gate();
  const running = [
    locks.run(["/data/a.txt"], async () => {
      events.push("a1 start");
      await first.promise;
      events.push("a1 end");
    }),
    locks.run(["/data/a.txt"], async () => {
      events.push("a2");
    }),
    locks.run(["/data/b.txt"], async () => {
      events.push("b");
    }),
  ];
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(events, ["a1 start", "b"]);
  first.open();
  await Promise.all(running);
  assert.deepEqual(events, ["a1 start", "b", "a1 end", "a2"]);
});

test("a failed operation releases its file for the next writer", async () => {
  const locks = new FileIdentityLocks();
  await assert.rejects(
    locks.run(["/data/a.txt"], async () => {
      throw new Error("write failed");
    }),
    { message: "write failed" },
  );
  assert.equal(await locks.run(["/data/a.txt"], async () => "next"), "next");
});

test("paths differing only in case are one file on Windows and macOS", async () => {
  const locks = new FileIdentityLocks();
  const events: string[] = [];
  const first = gate();
  const running = [
    locks.run(["/data/Notes.md"], async () => {
      events.push("upper start");
      await first.promise;
      events.push("upper end");
    }),
    locks.run(["/data/notes.md"], async () => {
      events.push("lower");
    }),
  ];
  await new Promise((resolve) => setImmediate(resolve));
  const caseInsensitive = process.platform === "win32" || process.platform === "darwin";
  assert.deepEqual(events, caseInsensitive ? ["upper start"] : ["upper start", "lower"]);
  first.open();
  await Promise.all(running);
});

test("holders of several files never wait on each other, and repeated paths are taken once", async () => {
  const locks = new FileIdentityLocks();
  const results = await Promise.all([
    locks.run(["/data/x", "/data/y"], async () => {
      await new Promise((resolve) => setImmediate(resolve));
      return "x-y";
    }),
    locks.run(["/data/y", "/data/x"], async () => {
      await new Promise((resolve) => setImmediate(resolve));
      return "y-x";
    }),
    locks.run(["/data/x", "/data/x"], async () => "x-x"),
  ]);
  assert.deepEqual(results, ["x-y", "y-x", "x-x"]);
});
