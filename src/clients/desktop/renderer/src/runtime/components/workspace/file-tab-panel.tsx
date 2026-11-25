"use client";

import * as React from "react";
import {
  ChatFilePane,
  FileDocumentProvider,
  useChatProject,
  useExecutionPlatform,
  type ChatFilePaneProps,
} from "@agw/chat";
import type { FileViewerFile } from "@agw/extension-sdk";

import { formatCloudUri } from "../../extensions/cloud-file-system";
import type { ExtensionHost, ExtensionHostSnapshot } from "../../extensions/extension-host";
import type { ExtensionLayoutSampler } from "../../extensions/extension-layout-sampler";
import { fileBaseName } from "../../extensions/file-viewer-patterns";
import { fileViewerInstanceKey } from "../../extensions/workspace-ids";
import { fileViewerDefault } from "../../extensions/workspace-registry";
import type { FileTab } from "../../extensions/workspace-tab";
import { useEditableSource } from "../../source/use-editable-source";
import { ExtensionView, ExtensionViewInfo, reloadExtensionView } from "./extension-view";

function SourceFloatingChatContext({
  host,
  context,
}: {
  host: ExtensionHost;
  context: Record<string, string>;
}) {
  React.useEffect(() => {
    host.setSourceFloatingChatContext(context);
  }, [host, context]);
  React.useEffect(() => () => host.closeSourceFloatingChat(), [host]);
  return null;
}

type FileTabPanelProps = {
  host: ExtensionHost;
  layout: ExtensionLayoutSampler;
  snapshot: ExtensionHostSnapshot;
  tab: FileTab;
  active: boolean;
  fileViewerDefaults: Readonly<Record<string, string>>;
  onSetDefaultFileViewer?: (fileName: string, viewerId: string | null) => void;
};

function useFileLocation({ directoryId, path }: FileTab) {
  const { selectedProjectId, projectDirectories } = useChatProject();
  const { serverId } = useExecutionPlatform();
  const directory = projectDirectories.find((item) => item.id === directoryId);
  const name = fileBaseName(path);
  return React.useMemo(() => {
    if (!selectedProjectId || !directory) return { name, file: null, sourceChatContext: null };
    const file: FileViewerFile = {
      name,
      projectId: selectedProjectId,
      uri: formatCloudUri({ serverId, projectId: selectedProjectId, directoryId, path }),
    };
    const sourceChatContext: Record<string, string> = {
      projectId: selectedProjectId,
      ...(directory.id ? { directoryId: directory.id } : {}),
      ...(directory.name ? { directoryName: directory.name } : {}),
      projectRelativePath: path,
    };
    return { name, file, sourceChatContext };
  }, [selectedProjectId, directory, name, serverId, directoryId, path]);
}

function useFileTabSource(
  host: ExtensionHost,
  { id, directoryId, path }: FileTab,
  file: FileViewerFile | null,
) {
  const { subscribeFileChanges } = useChatProject();
  const editableSource = useEditableSource(host, id, file);
  React.useEffect(
    () =>
      subscribeFileChanges((change) => {
        if (change.path === path && change.directoryId === directoryId) {
          host.sourceDocuments.get(id)?.state.reloadChanged();
        }
      }),
    [host, id, directoryId, path, subscribeFileChanges],
  );
  return editableSource;
}

function useFileViewers(props: FileTabPanelProps, file: FileViewerFile | null, name: string) {
  const { host, tab, fileViewerDefaults, snapshot } = props;
  const registry = React.useSyncExternalStore(
    host.registry.subscribe,
    host.registry.getSnapshot,
    host.registry.getSnapshot,
  );
  const instanceKeyOf = (viewerId: string) => fileViewerInstanceKey(tab.id, viewerId);
  const matching = file ? host.registry.fileViewersFor(name, fileViewerDefaults) : [];
  const fileViewers: ChatFilePaneProps["fileViewers"] = matching.map((view) => {
    const instanceKey = instanceKeyOf(view.id);
    const items = snapshot.fileHeaderItems[instanceKey] ?? [];
    return {
      id: view.id,
      title: view.title,
      unsaved: snapshot.unsavedInstanceKeys.includes(instanceKey),
      saveStatus: snapshot.saveStatuses[instanceKey] ?? null,
      topItems: items.filter((item) => item.placement === "top"),
      bottomItems: items.filter((item) => item.placement === "bottom"),
    };
  });
  return {
    fileViewers,
    viewFor: (viewerId: string) => registry.views.get(viewerId),
    instanceKeyOf,
  };
}

function fileViewerControls(
  { host, layout, snapshot, active }: FileTabPanelProps,
  file: FileViewerFile | null,
  { viewFor, instanceKeyOf }: ReturnType<typeof useFileViewers>,
): Pick<
  ChatFilePaneProps,
  | "renderFileViewer"
  | "onReloadFileViewer"
  | "onReloadFileViewerDocument"
  | "renderFileViewerInfo"
  | "onSelectFileViewerItem"
> {
  const renderFileViewer: ChatFilePaneProps["renderFileViewer"] = (viewerId) => {
    const view = viewFor(viewerId);
    return view && file ? (
      <ExtensionView
        key={viewerId}
        host={host}
        layout={layout}
        snapshot={snapshot}
        view={view}
        active={active}
        instanceKey={instanceKeyOf(viewerId)}
        openRequest={undefined}
        file={file}
        showHeader={false}
      />
    ) : null;
  };
  return {
    renderFileViewer,
    onReloadFileViewer: (viewerId) => {
      const view = viewFor(viewerId);
      if (view) void reloadExtensionView(host, view, instanceKeyOf(viewerId));
    },
    onReloadFileViewerDocument: (viewerId) =>
      host.reloadFileViewerDocument(instanceKeyOf(viewerId)),
    renderFileViewerInfo: (viewerId) => {
      const view = viewFor(viewerId);
      return view ? <ExtensionViewInfo view={view} /> : null;
    },
    onSelectFileViewerItem: (viewerId, itemId, entryId) =>
      host.selectFileHeaderItem(instanceKeyOf(viewerId), itemId, entryId),
  };
}

function FileTabContent(props: FileTabPanelProps & ReturnType<typeof useFileLocation>) {
  const {
    host,
    tab,
    active,
    fileViewerDefaults,
    onSetDefaultFileViewer,
    file,
    name,
    sourceChatContext,
  } = props;
  const editableSource = useFileTabSource(host, tab, file);
  const viewers = useFileViewers(props, file, name);
  return (
    <ChatFilePane
      fileViewers={viewers.fileViewers}
      {...fileViewerControls(props, file, viewers)}
      defaultFileViewerId={fileViewerDefault(fileViewerDefaults, name)}
      onSetDefaultFileViewer={
        onSetDefaultFileViewer && ((viewerId) => onSetDefaultFileViewer(name, viewerId))
      }
      editableSource={editableSource}
      renderSourceAccessory={() =>
        active && sourceChatContext ? (
          <SourceFloatingChatContext host={host} context={sourceChatContext} />
        ) : null
      }
    />
  );
}

export function FileTabPanel(props: FileTabPanelProps) {
  const { host, tab } = props;
  const { id, directoryId, path, diffScope, diffFileView } = tab;
  const location = useFileLocation(tab);
  return (
    <FileDocumentProvider
      directoryId={directoryId}
      path={path}
      diffScope={diffScope}
      diffFileView={diffFileView}
      onDiffFileViewChange={(value) =>
        host.tabs.setFileView(id, { diffScope, diffFileView: value })
      }
      readSourceContent={false}
    >
      <FileTabContent {...props} {...location} />
    </FileDocumentProvider>
  );
}
