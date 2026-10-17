import { join } from "node:path";
import { ipcMain, type IpcMainInvokeEvent } from "electron";

import type {
  CreateExtensionFrameRequest,
  DesktopRuntimeState,
  ExtensionCaller,
  ExtensionFileSystemRequest,
  ExtensionFrameLayoutUpdate,
  ExtensionGrants,
  ExtensionHttpRequest,
  ExtensionInstallSource,
  ExtensionOpenDialogOptions,
  ExtensionSaveDialogOptions,
  ExtensionWatchRequest,
  UninstallExtensionOptions,
} from "../../shared/contracts";
import { toExtensionIpcResult } from "./extension-ipc-result";
import type { ExtensionRuntime } from "./extension-runtime";

const EXTENSION_ID_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/u;

export type ExtensionIpcOptions = {
  runtime: ExtensionRuntime;
  assertTrusted(event: IpcMainInvokeEvent): void;
  runtimeState(): Promise<DesktopRuntimeState>;
};

function assertExtensionId(extensionId: string): void {
  if (!EXTENSION_ID_PATTERN.test(extensionId)) throw new Error("Invalid extension id.");
}

/** 注册 extension 的管理、frame、能力通道与 extension preload 通道。Registers the extension IPC channels. */
export function registerExtensionIpc({
  runtime,
  assertTrusted,
  runtimeState,
}: ExtensionIpcOptions): void {
  const handle = <A extends unknown[], R>(
    channel: string,
    handler: (...args: A) => R | Promise<R>,
  ) =>
    ipcMain.handle(channel, async (event, ...args) => {
      assertTrusted(event);
      return handler(...(args as A));
    });
  const capability = <A extends unknown[], R>(
    channel: string,
    handler: (...args: A) => Promise<R> | R,
  ) =>
    ipcMain.handle(channel, async (event, ...args) => {
      assertTrusted(event);
      return toExtensionIpcResult(async () => handler(...(args as A)));
    });

  /* 管理通道 Management channels */
  handle("agw:attach-extension-host", (hostInstanceId: string) =>
    runtime.attachHost(hostInstanceId),
  );
  handle("agw:list-extensions", () => runtime.listExtensions());
  handle("agw:list-extension-recovery-notices", () => runtime.listRecoveryNotices());
  handle("agw:prepare-extension-install", (request: { source: ExtensionInstallSource }) =>
    runtime.prepareInstall(request.source),
  );
  handle("agw:commit-extension-install", (installId: string) => runtime.commitInstall(installId));
  handle("agw:cancel-extension-install", (installId: string) => runtime.cancelInstall(installId));
  handle(
    "agw:uninstall-extension",
    async (extensionId: string, options: UninstallExtensionOptions) => {
      assertExtensionId(extensionId);
      await runtime.uninstall(extensionId, options.deleteData);
      return runtimeState();
    },
  );
  handle(
    "agw:set-extension-enabled",
    async (extensionId: string, enabled: boolean, grants?: ExtensionGrants) => {
      await runtime.setEnabled(extensionId, enabled, grants);
      return runtimeState();
    },
  );
  handle("agw:begin-extension-activation", (hostInstanceId: string, extensionId: string) =>
    runtime.beginActivation(hostInstanceId, extensionId),
  );
  handle("agw:end-extension-activation", (hostInstanceId: string, extensionId: string) =>
    runtime.endActivation(hostInstanceId, extensionId),
  );
  handle(
    "agw:record-extension-activation",
    async (hostInstanceId: string, extensionId: string, version: string) => {
      await runtime.recordActivation(hostInstanceId, extensionId, version);
      return runtimeState();
    },
  );
  handle(
    "agw:release-extension-frame-resources",
    (hostInstanceId: string, extensionId: string, frameId: string) =>
      runtime.releaseFrameResources(hostInstanceId, extensionId, frameId),
  );
  handle("agw:release-extension-resources", (hostInstanceId: string, extensionId: string) =>
    runtime.releaseResources(hostInstanceId, extensionId),
  );
  handle("agw:resume-extensions", async () => {
    runtime.safeMode.resume();
    return runtimeState();
  });
  handle("agw:open-extensions-directory", () => runtime.openDirectory(runtime.extensionsDirectory));
  handle("agw:open-extension-data-directory", (extensionId: string) => {
    assertExtensionId(extensionId);
    return runtime.openDirectory(join(runtime.dataDirectory, extensionId));
  });
  handle("agw:cancel-extension-http-request", (requestId: string) =>
    runtime.cancelHttpRequest(requestId),
  );
  handle("agw:save-extension-sdk-types", () => runtime.saveSdkTypes());

  /* frame 通道 Frame channels */
  handle(
    "agw:create-extension-frame",
    (hostInstanceId: string, request: CreateExtensionFrameRequest) =>
      runtime.createFrame(hostInstanceId, request),
  );
  handle(
    "agw:update-extension-frame-layout",
    (hostInstanceId: string, updates: ExtensionFrameLayoutUpdate[]) =>
      runtime.updateFrameLayout(hostInstanceId, updates),
  );
  handle("agw:obscure-extension-frames", (hostInstanceId: string, frameIds: string[]) =>
    runtime.obscureFrames(hostInstanceId, frameIds),
  );
  handle("agw:reveal-extension-frames", (hostInstanceId: string, frameIds: string[]) =>
    runtime.revealFrames(hostInstanceId, frameIds),
  );
  handle("agw:set-extension-frame-background", (hostInstanceId: string, color: string) =>
    runtime.setFrameBackground(hostInstanceId, color),
  );
  handle("agw:destroy-extension-frame", (hostInstanceId: string, frameId: string) =>
    runtime.destroyFrame(hostInstanceId, frameId),
  );
  handle("agw:terminate-extension-frame-process", (hostInstanceId: string, frameId: string) =>
    runtime.terminateFrameProcess(hostInstanceId, frameId),
  );
  handle("agw:open-extension-frame-dev-tools", (hostInstanceId: string, frameId: string) =>
    runtime.openFrameDevTools(hostInstanceId, frameId),
  );

  /* 能力通道 Capability channels */
  capability(
    "agw:extension-file-system",
    (caller: ExtensionCaller, request: ExtensionFileSystemRequest) =>
      runtime.fileSystem(caller, request),
  );
  capability("agw:extension-watch", (caller: ExtensionCaller, request: ExtensionWatchRequest) =>
    runtime.watch(caller, request),
  );
  capability("agw:extension-unwatch", (caller: ExtensionCaller, watchId: string) =>
    runtime.unwatch(caller, watchId),
  );
  capability(
    "agw:extension-open-dialog",
    (caller: ExtensionCaller, options: ExtensionOpenDialogOptions) =>
      runtime.showOpenDialog(caller, options),
  );
  capability(
    "agw:extension-save-dialog",
    (caller: ExtensionCaller, options: ExtensionSaveDialogOptions) =>
      runtime.showSaveDialog(caller, options),
  );
  capability("agw:reveal-extension-path", (caller: ExtensionCaller, uri: string) =>
    runtime.reveal(caller, uri),
  );
  capability(
    "agw:extension-http-request",
    (caller: ExtensionCaller, request: ExtensionHttpRequest) =>
      runtime.httpRequest(caller, request),
  );

  /* extension preload 通道：身份由主进程登记的 WebContents 决定。Identity comes from the registered WebContents. */
  ipcMain.on("agw-extension:hello", (event, payload: unknown) =>
    runtime.handleHello(event, payload),
  );
  ipcMain.on("agw-extension:csp-violation", (event, payload: unknown) =>
    runtime.handleCspViolation(event, payload),
  );
}
