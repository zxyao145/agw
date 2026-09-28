import { spawn } from "node:child_process";
import { once } from "node:events";
import { context } from "esbuild";

import { mainBuildOptions } from "./build-main.mjs";

export async function watchMain(launch) {
  let electron;
  let stopping = false;
  const closed = Promise.withResolvers();

  async function stopElectron() {
    const previous = electron;
    if (!previous) return;
    electron = undefined;
    const exited = once(previous, "exit");
    // Native menus can suspend Node signal handlers while open.
    // 原生菜单打开期间可能暂停 Node 信号处理。
    previous.kill("SIGKILL");
    await exited;
  }

  const buildContext = await context({
    ...mainBuildOptions,
    plugins: [
      {
        name: "restart-electron",
        setup(build) {
          build.onEnd(async (result) => {
            if (stopping) return;
            if (result.errors.length > 0) {
              closed.reject(new Error("Desktop main process build failed."));
              return;
            }
            try {
              await stopElectron();
              if (stopping) return;
              const child = spawn(launch.command, launch.args, launch.options);
              electron = child;
              child.once("error", (error) => {
                electron = undefined;
                closed.reject(error);
              });
              child.once("exit", (code, signal) => {
                if (electron !== child) return;
                electron = undefined;
                if (code === 0) closed.resolve();
                else closed.reject(new Error(`Electron exited with ${signal ?? `code ${code}`}`));
              });
            } catch (error) {
              closed.reject(error);
            }
          });
        },
      },
    ],
  });
  await buildContext.watch();

  return {
    closed: closed.promise,
    async dispose() {
      stopping = true;
      await buildContext.dispose();
      await stopElectron();
    },
  };
}
