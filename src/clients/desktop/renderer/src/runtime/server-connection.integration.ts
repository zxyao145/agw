import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { mkdir, mkdtemp, readFile, rm } from "node:fs/promises";
import { createServer } from "node:net";
import { resolve } from "node:path";
import test from "node:test";

import type { DesktopRuntimeState } from "./runtime-model";
import { connectServer } from "./server-connection";

test("connectServer_DelayedStandaloneStartup_FollowsRuntimePortAndConnects", async (t) => {
  const root = resolve(import.meta.dirname, "../../../../../..");
  const scratch = resolve(root, "src/clients/desktop/.scratch");
  await mkdir(scratch, { recursive: true });
  const directory = await mkdtemp(resolve(scratch, "server-startup-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const socket = createServer();
  await new Promise<void>((done) => socket.listen(0, "127.0.0.1", done));
  const address = socket.address();
  assert.ok(address && typeof address !== "string");
  await new Promise<void>((done) => socket.close(() => done()));
  const state: DesktopRuntimeState = {
    isDesktop: true,
    platform: "darwin",
    architecture: "arm64",
    appVersion: "1.0.0",
    packageFlavor: "full",
    activeToken: null,
    localServerRuntime: null,
    extensionSafeMode: null,
    settings: {
      schemaVersion: 1,
      packageFlavor: "full",
      closeBehavior: "minimize-to-tray",
      activeServerId: "local",
      projectTabsByServer: {},
      extensions: {},
      removedBundledExtensions: [],
      fileViewerDefaults: {},
      appearance: { baseFontSize: 16, chatFontSize: null, codeFontSize: null, theme: "default" },
      profiles: [
        {
          id: "local",
          kind: "local",
          name: "Local",
          baseUrl: `http://127.0.0.1:${address.port}`,
          apiMajorVersion: 1,
          allowInsecureHttp: true,
        },
      ],
    },
  };
  const readState = async (): Promise<DesktopRuntimeState> => {
    try {
      const localServerRuntime = JSON.parse(
        await readFile(resolve(directory, "runtime/server.json"), "utf8"),
      );
      return { ...state, localServerRuntime };
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") return state;
      throw error;
    }
  };
  const controller = new AbortController();
  t.after(() => controller.abort());
  const pending = connectServer(state, readState, controller.signal);
  let settled = false;
  void pending.then(
    () => {
      settled = true;
    },
    () => {
      settled = true;
    },
  );
  await new Promise((done) => setTimeout(done, 1_200));
  assert.equal(settled, false);

  const server = spawn(
    "dotnet",
    [
      resolve(root, "src/server/Agw.Standalone.Host/bin/Debug/net10.0/agw-server.dll"),
      `--AgwDataDir=${directory}`,
      "--urls=http://127.0.0.1:0",
    ],
    { cwd: root, stdio: ["ignore", "pipe", "pipe"] },
  );
  let output = "";
  server.stdout.on("data", (chunk) => {
    output += chunk.toString();
  });
  server.stderr.on("data", (chunk) => {
    output += chunk.toString();
  });
  t.after(async () => {
    if (server.exitCode === null) {
      const exited = once(server, "exit");
      server.kill("SIGTERM");
      await exited;
    }
  });
  const connected = await pending.catch((error) => {
    throw new Error(output, { cause: error });
  });
  assert.equal(connected.info.apiMajorVersion, 1);
  assert.equal(typeof connected.info.initialized, "boolean");
  assert.ok(connected.state.localServerRuntime);
  assert.notEqual(connected.state.localServerRuntime.port, address.port);

  const alreadyRunning = await connectServer(connected.state, readState, controller.signal);
  assert.deepEqual(alreadyRunning.info, connected.info);
});
