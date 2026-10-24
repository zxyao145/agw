import { join } from "node:path";
import { ipcMain, type IpcMainInvokeEvent } from "electron";

import type {
  CreateExtensionFrameRequest,
  DesktopRuntimeState,
  EncodeTextOptions,
  ExtensionCaller,
  ExtensionFileSystemRequest,
  ExtensionFrameLayoutUpdate,
  ExtensionFrameObscureRequest,
  ExtensionHttpRequest,
  HostConfirmationRequest,
  ExtensionInstallSource,
  ExtensionOpenDialogOptions,
  ExtensionSaveDialogOptions,
  ExtensionWatchRequest,
  ThemeStyleRequest,
  UninstallExtensionOptions,
} from "../../shared/contracts";
import type { ConfirmationService } from "../confirmation/confirmation-service";
import { canceledAnswer, hostConfirmationView } from "../confirmation/host-confirmation";
import { toExtensionIpcResult } from "./extension-ipc-result";
import type { ExtensionRuntime } from "./extension-runtime";
import { decodeText, encodeText, listTextEncodings } from "./text-codec";

const EXTENSION_ID_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/u;

export type ExtensionIpcOptions = {
  runtime: ExtensionRuntime;
  confirmations: ConfirmationService;
  assertTrusted(event: IpcMainInvokeEvent): void;
  runtimeState(): Promise<DesktopRuntimeState>;
};

function assertExtensionId(extensionId: string): void {
  if (!EXTENSION_ID_PATTERN.test(extensionId)) throw new Error("Invalid extension id.");
}

/** 注册 extension 的管理、frame、能力通道与 extension preload 通道。Registers the extension IPC channels. */
export function registerExtensionIpc({
  runtime,
  confirmations,
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

  /* 确认通道：在独立的确认窗口中显示，宿主页面只得到回答 Confirmation channels: shown in the separate confirmation window; the host page only gets the answer */
  ipcMain.handle("agw:confirm-host-action", async (event, request: HostConfirmationRequest) => {
    assertTrusted(event);
    const { view, clientId } = hostConfirmationView(request);
    const answer = await confirmations.request(view, {
      owner: event.sender,
      ...(clientId === null ? {} : { clientId }),
    });
    return answer ?? canceledAnswer(view);
  });
  ipcMain.handle("agw:dismiss-host-confirmation", (event, clientId: string) => {
    assertTrusted(event);
    confirmations.dismiss(event.sender, String(clientId));
  });
  handle("agw:approve-extension-install", (installId: string) => runtime.approveInstall(installId));
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
  handle("agw:set-extension-enabled", (extensionId: string, enabled: boolean) => {
    assertExtensionId(extensionId);
    return runtime.setEnabled(extensionId, enabled === true);
  });
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
  handle("agw:open-extensions-directory", () => runtime.openDirectory(runtime.packagesDirectory));
  handle("agw:open-extension-data-directory", (extensionId: string) => {
    assertExtensionId(extensionId);
    return runtime.openDirectory(join(runtime.dataDirectory, extensionId));
  });
  handle("agw:cancel-extension-http-request", (requestId: string) =>
    runtime.cancelHttpRequest(requestId),
  );
  handle("agw:save-extension-sdk-types", () => runtime.saveSdkTypes());
  handle("agw:reload-extension", (extensionId: string) => {
    assertExtensionId(extensionId);
    return runtime.reloadExtension(extensionId);
  });
  handle("agw:get-theme-style", (request: ThemeStyleRequest) => runtime.getThemeStyle(request));

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
  handle(
    "agw:hide-obscured-extension-frames",
    (hostInstanceId: string, requests: ExtensionFrameObscureRequest[]) =>
      runtime.hideObscuredFrames(hostInstanceId, requests),
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

  /* 文本编码通道：只供宿主页面调用，错误码与能力通道一样原样返回。Text codec channels for the host page; error codes come back like capability channels. */
  capability("agw:decode-text", (bytes: Uint8Array, encoding?: string) =>
    decodeText(bytes, encoding),
  );
  capability("agw:encode-text", (text: string, options: EncodeTextOptions) =>
    encodeText(text, options),
  );
  handle("agw:list-text-encodings", () => listTextEncodings());

  /* extension preload 通道：身份由主进程登记的 WebContents 决定。Identity comes from the registered WebContents. */
  ipcMain.on("agw-extension:hello", (event, payload: unknown) =>
    runtime.handleHello(event, payload),
  );
  ipcMain.on("agw-extension:csp-violation", (event, payload: unknown) =>
    runtime.handleCspViolation(event, payload),
  );
}
