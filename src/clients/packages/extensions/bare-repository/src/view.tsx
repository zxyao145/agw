import {
  defineView,
  type ContextProject,
  type ExtensionApi,
  type HostContext,
} from "@agw/extension-sdk";
import { Component, useEffect, useState, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

/** 每次打开视图的参数与序号。The params of each view open, with a sequence number. */
export type OpenRequest = { params: unknown; sequence: number };

/** 只读文件主视图的参数。The params of the read-only file main view. */
export type FileParams = { repository: string; branch: string; path: string };

export function errorMessage(caught: unknown): string {
  return caught instanceof Error ? caught.message : String(caught);
}

/**
 * 渲染出错时显示错误而不是空白页，下一次打开请求时重试。Shows the error instead of a blank page when rendering fails, and
 * retries on the next open request.
 */
class ErrorBoundary extends Component<
  { resetKey: number; children: ReactNode },
  { error: unknown; resetKey: number }
> {
  public override state = { error: null as unknown, resetKey: this.props.resetKey };

  public static getDerivedStateFromError(error: unknown) {
    return { error };
  }

  public static getDerivedStateFromProps(
    props: { resetKey: number },
    state: { error: unknown; resetKey: number },
  ) {
    return props.resetKey === state.resetKey ? null : { error: null, resetKey: props.resetKey };
  }

  public override render() {
    return this.state.error ? (
      <main className="centered">
        <p className="error" role="alert">
          {errorMessage(this.state.error)}
        </p>
      </main>
    ) : (
      this.props.children
    );
  }
}

function App({
  api,
  request,
  render,
}: {
  api: ExtensionApi;
  request: OpenRequest;
  render: (props: {
    api: ExtensionApi;
    context: HostContext;
    project: ContextProject;
    request: OpenRequest;
  }) => ReactNode;
}) {
  const [context, setContext] = useState(api.context.get());
  useEffect(() => {
    const subscription = api.context.onDidChange(setContext);
    return () => subscription.dispose();
  }, [api]);
  return context.project ? (
    <div key={`${context.server.id}/${context.project.id}`} className="view">
      {render({ api, context, project: context.project, request })}
    </div>
  ) : (
    <main className="centered">
      <p>Select a Project to work with its repositories.</p>
    </main>
  );
}

/** 挂载视图；`onLoad` 与 `onShow` 的参数作为打开请求传入。Mounts a view; `onLoad` and `onShow` params become open requests. */
export function startView(
  render: (props: {
    api: ExtensionApi;
    context: HostContext;
    project: ContextProject;
    request: OpenRequest;
  }) => ReactNode,
): void {
  const root = createRoot(document.getElementById("root")!);
  let api: ExtensionApi | null = null;
  let sequence = 0;
  const show = (params: unknown) => {
    if (api)
      root.render(
        <ErrorBoundary resetKey={++sequence}>
          <App api={api} request={{ params, sequence }} render={render} />
        </ErrorBoundary>,
      );
  };
  defineView({
    onLoad(loaded, params) {
      api = loaded;
      api.theme.apply();
      show(params);
    },
    onShow(params) {
      show(params);
    },
    onUnload() {
      root.unmount();
    },
  });
}
