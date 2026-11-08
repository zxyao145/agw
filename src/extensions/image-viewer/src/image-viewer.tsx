import type { ExtensionApi, FileViewerFile } from "@agw/extension-sdk";
import { useEffect, useEffectEvent, useLayoutEffect, useRef, useState } from "react";

import {
  type DecodedImage,
  decodeImage,
  formatFileSize,
  imageMimeType,
  sameBytes,
} from "./image-file";
import {
  fitScale,
  imagePointUnder,
  layoutImage,
  PIXELATED_SCALE,
  type Point,
  scrollToPoint,
  type Size,
  wheelScale,
  zoomIn,
  zoomOut,
} from "./zoom";

const DECODE_FAILED = "The file is not a valid image, or its format is not supported.";

type ViewState =
  | { status: "loading" }
  | { status: "ready"; image: DecodedImage }
  | { status: "failed"; title: string; message: string };

/** 适应显示区，或固定的比例。Fit the display area, or a fixed scale. */
type Zoom = { mode: "fit" } | { mode: "scale"; scale: number };

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export interface ImageViewerProps {
  api: ExtensionApi;
  file: FileViewerFile;
  /**
   * 同一个文件每次再次显示时加 1，查看器随之重新读取文件。
   * Incremented on every show of the same file; the viewer reads the file again.
   */
  revision: number;
}

export function ImageViewer({ api, file, revision }: ImageViewerProps) {
  const type = imageMimeType(file.name);
  const [state, setState] = useState<ViewState>({ status: "loading" });
  // 正在显示的图片，重新读取时与读到的字节比较。The image being shown, compared with the bytes read again.
  const shown = useRef<DecodedImage | null>(null);
  const url = state.status === "ready" ? state.image.url : null;

  useEffect(() => {
    shown.current = state.status === "ready" ? state.image : null;
  }, [state]);

  // 图片被替换或页面卸载时释放 Blob URL。Revokes the Blob URL once the image is replaced or the page unloads.
  useEffect(() => {
    if (url === null) return undefined;
    return () => URL.revokeObjectURL(url);
  }, [url]);

  // 读取并解码；字节与正在显示的图片相同时保持不变，解码完成之前继续显示旧图片。
  // Reads and decodes; identical bytes keep the shown image, which stays until the new one is decoded.
  useEffect(() => {
    const controller = new AbortController();
    const { signal } = controller;
    void (async () => {
      let bytes: Uint8Array;
      try {
        bytes = await api.fs.readFile(file.uri, { signal });
      } catch (error) {
        if (signal.aborted) return;
        setState({
          status: "failed",
          title: `Could not read ${file.name}`,
          message: errorMessage(error),
        });
        return;
      }
      if (signal.aborted) return;
      const current = shown.current;
      if (current !== null && sameBytes(current.bytes, bytes)) return;
      let image: DecodedImage;
      try {
        image = await decodeImage(bytes, type);
      } catch {
        if (signal.aborted) return;
        setState({
          status: "failed",
          title: `Could not display ${file.name}`,
          message: DECODE_FAILED,
        });
        return;
      }
      if (signal.aborted) {
        URL.revokeObjectURL(image.url);
        return;
      }
      setState({ status: "ready", image });
    })();
    return () => controller.abort();
  }, [api, file.uri, file.name, type, revision]);

  return (
    <div className="image-viewer">
      {state.status === "loading" ? <p className="status">Loading {file.name}…</p> : null}
      {state.status === "failed" ? (
        <div className="image-error" role="alert">
          <p className="image-error-title">{state.title}</p>
          <p>{state.message}</p>
        </div>
      ) : null}
      {state.status === "ready" ? <ImageStage image={state.image} name={file.name} /> : null}
    </div>
  );
}

/** 显示区与状态栏；替换图片时保留缩放。The display area and the status bar; the zoom stays when the image is replaced. */
function ImageStage({ image, name }: { image: DecodedImage; name: string }) {
  const stageRef = useRef<HTMLDivElement>(null);
  const [stage, setStage] = useState<Size | null>(null);
  const [zoom, setZoom] = useState<Zoom>({ mode: "fit" });
  // 已经请求、尚未应用的缩放：比例，以及要停在 anchor（显示区坐标；null 为应用时显示区的中心）下的图片点；
  // 连续的滚轮事件以它为起点。
  // The requested zoom not yet applied: the scale and the image point to keep under the anchor (display-area
  // coordinates; null for the display area's center when applied); consecutive wheel events start from it.
  const pending = useRef<{ scale: number; point: Point; anchor: Point | null } | null>(null);
  const drag = useRef<{ pointerId: number; x: number; y: number; scroll: Point } | null>(null);
  const [dragging, setDragging] = useState(false);

  const natural: Size = { width: image.width, height: image.height };
  const fit = stage === null ? null : fitScale(natural, stage);
  const scale = zoom.mode === "fit" ? fit : zoom.scale;
  const layout = stage === null || scale === null ? null : layoutImage(natural, stage, scale);
  const pannable =
    stage !== null &&
    layout !== null &&
    (layout.content.width > stage.width || layout.content.height > stage.height);

  // 窗口等外部原因引起的尺寸变化。Size changes from outside, such as the window.
  useLayoutEffect(() => {
    const element = stageRef.current!;
    const observer = new ResizeObserver(() =>
      setStage({ width: element.clientWidth, height: element.clientHeight }),
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  // 每次提交后测量可见尺寸（不含滚动条）：内容区使滚动条出现或消失时，ResizeObserver 不报告这一变化。尺寸稳定、
  // 比例已经提交后，再应用等待中的缩放的滚动位置。
  // Measures the visible size (without scrollbars) after every commit: ResizeObserver does not report scrollbars that
  // the content adds or removes. The pending zoom's scroll position applies once the size is stable and its scale is
  // committed.
  useLayoutEffect(() => {
    const element = stageRef.current!;
    const measured = { width: element.clientWidth, height: element.clientHeight };
    if (stage === null || measured.width !== stage.width || measured.height !== stage.height) {
      setStage(measured);
      return;
    }
    const target = pending.current;
    if (target === null || target.scale !== scale) return;
    pending.current = null;
    const scroll = scrollToPoint({ image: natural, stage, ...target });
    element.scrollLeft = scroll.x;
    element.scrollTop = scroll.y;
  });

  /** 缩放到 `next`，`anchor`（显示区坐标）下的点不动；null 时为显示区中心。Zooms keeping the point under `anchor`. */
  const zoomTo = (next: number, anchor: Point | null) => {
    if (stage === null || scale === null) return;
    const element = stageRef.current!;
    // 等待中的缩放尚未应用时，从它的比例与滚动位置开始。A pending zoom not yet applied is the starting point.
    const current = pending.current;
    const scroll =
      current === null
        ? { x: element.scrollLeft, y: element.scrollTop }
        : scrollToPoint({ image: natural, stage, ...current });
    pending.current = {
      scale: next,
      point: imagePointUnder({
        image: natural,
        stage,
        scale: current?.scale ?? scale,
        scroll,
        anchor,
      }),
      anchor,
    };
    setZoom({ mode: "scale", scale: next });
  };

  // 带 Ctrl 或 Cmd 的滚轮与触控板捏合按指针位置缩放；其他滚轮由浏览器滚动。
  // Wheel events with Ctrl or Cmd, pinches included, zoom at the pointer; other wheel events scroll natively.
  const onWheel = useEffectEvent((event: WheelEvent) => {
    if (!event.ctrlKey && !event.metaKey) return;
    event.preventDefault();
    if (stage === null || fit === null || scale === null) return;
    const rect = stageRef.current!.getBoundingClientRect();
    const from = pending.current?.scale ?? scale;
    zoomTo(wheelScale(from, event, stage.height, fit), {
      x: event.clientX - rect.left,
      y: event.clientY - rect.top,
    });
  });
  useEffect(() => {
    const element = stageRef.current!;
    const listener = (event: WheelEvent) => onWheel(event);
    // preventDefault 需要非 passive 的监听器。preventDefault needs a non-passive listener.
    element.addEventListener("wheel", listener, { passive: false });
    return () => element.removeEventListener("wheel", listener);
  }, []);

  const endDrag = (event: React.PointerEvent<HTMLDivElement>) => {
    if (drag.current?.pointerId !== event.pointerId) return;
    drag.current = null;
    setDragging(false);
  };

  const nextIn = scale === null ? null : zoomIn(scale);
  const nextOut = scale === null ? null : zoomOut(scale);

  return (
    <>
      <div
        ref={stageRef}
        className="image-stage"
        data-pannable={pannable || undefined}
        data-dragging={dragging || undefined}
        onPointerDown={(event) => {
          const element = event.currentTarget;
          const rect = element.getBoundingClientRect();
          // 滚动条上的按下由浏览器处理。Presses on the scrollbars stay with the browser.
          const onScrollbar =
            event.clientX - rect.left >= element.clientWidth ||
            event.clientY - rect.top >= element.clientHeight;
          if (event.button !== 0 || !pannable || onScrollbar) return;
          element.setPointerCapture(event.pointerId);
          drag.current = {
            pointerId: event.pointerId,
            x: event.clientX,
            y: event.clientY,
            scroll: { x: element.scrollLeft, y: element.scrollTop },
          };
          setDragging(true);
        }}
        onPointerMove={(event) => {
          const start = drag.current;
          if (start?.pointerId !== event.pointerId) return;
          event.currentTarget.scrollLeft = start.scroll.x - (event.clientX - start.x);
          event.currentTarget.scrollTop = start.scroll.y - (event.clientY - start.y);
        }}
        onPointerUp={endDrag}
        onPointerCancel={endDrag}
        onLostPointerCapture={endDrag}
      >
        {layout === null ? null : (
          <div
            className="image-content"
            style={{ width: layout.content.width, height: layout.content.height }}
          >
            <img
              src={image.url}
              alt={name}
              draggable={false}
              className={scale !== null && scale >= PIXELATED_SCALE ? "pixelated" : undefined}
              style={{
                left: layout.left,
                top: layout.top,
                width: layout.width,
                height: layout.height,
              }}
            />
          </div>
        )}
      </div>
      <div className="image-status-bar">
        <span data-image-size>
          {image.width} × {image.height}
        </span>
        <span data-file-size>{formatFileSize(image.bytes.length)}</span>
        <span className="image-status-spacer" />
        <button
          type="button"
          className="chrome-button"
          aria-label="Zoom out"
          title="Zoom out"
          disabled={nextOut === null}
          onClick={() => zoomTo(nextOut!, null)}
        >
          −
        </button>
        <span className="image-zoom" data-zoom>
          {scale === null ? null : `${Math.round(scale * 100)}%`}
        </span>
        <button
          type="button"
          className="chrome-button"
          aria-label="Zoom in"
          title="Zoom in"
          disabled={nextIn === null}
          onClick={() => zoomTo(nextIn!, null)}
        >
          +
        </button>
        <button
          type="button"
          className="chrome-button"
          title="Fit to window"
          aria-pressed={zoom.mode === "fit"}
          onClick={() => {
            pending.current = null;
            setZoom({ mode: "fit" });
          }}
        >
          Fit
        </button>
        <button
          type="button"
          className="chrome-button"
          aria-label="Actual size"
          title="Actual size"
          onClick={() => zoomTo(1, null)}
        >
          1:1
        </button>
      </div>
    </>
  );
}
