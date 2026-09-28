"use client";

import * as React from "react";
import { Minus, Plus } from "lucide-react";
import Lightbox, { type ZoomRef } from "yet-another-react-lightbox";
import Zoom from "yet-another-react-lightbox/plugins/zoom";

import { type ConversationImageSource, useImageContextMenu } from "./conversation-image";

export function ConversationImagePreview({
  image,
  trigger,
  scrollElementRef,
  onClose,
}: {
  image: ConversationImageSource;
  trigger: HTMLImageElement;
  scrollElementRef: React.RefObject<HTMLDivElement | null>;
  onClose: () => void;
}) {
  const slides = React.useMemo(() => [image], [image]);
  const onContextMenu = useImageContextMenu(image.name);
  const restoreScroll = React.useRef<() => void>(() => {});
  React.useLayoutEffect(() => {
    const positions: { element: HTMLElement; top: number; left: number }[] = [];
    for (let element = trigger.parentElement; element; element = element.parentElement) {
      positions.push({ element, top: element.scrollTop, left: element.scrollLeft });
    }
    restoreScroll.current = () => {
      for (const { element, top, left } of positions) {
        element.scrollTop = top;
        element.scrollLeft = left;
      }
    };
  }, [trigger]);

  return (
    <Lightbox
      open
      close={() => {
        const target = trigger.isConnected ? trigger : scrollElementRef.current;
        target?.focus({ preventScroll: true });
        restoreScroll.current();
        onClose();
      }}
      slides={slides}
      plugins={[Zoom]}
      className="agw-image-preview"
      labels={{ Lightbox: "Image preview" }}
      carousel={{ finite: true, preload: 0, padding: "64px", imageProps: { onContextMenu } }}
      controller={{ closeOnBackdropClick: true, disableSwipeNavigation: true }}
      zoom={{ scrollToZoom: true, maxZoomPixelRatio: 4 }}
      render={{
        buttonPrev: () => null,
        buttonNext: () => null,
        buttonZoom: (zoom) => <ImageZoomControls {...zoom} />,
        iconError: () => <span role="alert">Unable to load image</span>,
      }}
    />
  );
}

function ImageZoomControls({ zoom, maxZoom, zoomIn, zoomOut, disabled }: ZoomRef) {
  const groupRef = React.useRef<HTMLDivElement>(null);
  const focusedControl = React.useRef<HTMLButtonElement | null>(null);
  const zoomOutDisabled = disabled || zoom <= 1;
  const zoomInDisabled = disabled || zoom >= maxZoom;

  // 聚焦的按钮被禁用时浏览器会把焦点移到 body，Lightbox 就收不到 Esc 与方向键。
  // 把焦点交给仍可用的按钮，键盘操作保持在预览内。
  // Disabling the focused button makes the browser move focus to body, so the Lightbox stops
  // receiving Escape and arrow keys. Hand focus to the control that still works instead.
  React.useLayoutEffect(() => {
    const control = focusedControl.current;
    if (!control?.disabled) return;
    const active = document.activeElement;
    if (active !== control && active !== document.body && active !== null) return;
    focusedControl.current = null;
    const available = groupRef.current?.querySelector<HTMLButtonElement>("button:not([disabled])");
    (available ?? groupRef.current?.closest<HTMLElement>(".yarl__container"))?.focus();
  }, [zoomInDisabled, zoomOutDisabled]);

  return (
    <div
      ref={groupRef}
      className="agw-image-zoom"
      role="group"
      aria-label="Image zoom"
      onFocus={(event) => {
        focusedControl.current = event.target instanceof HTMLButtonElement ? event.target : null;
      }}
    >
      <button type="button" aria-label="Zoom out" disabled={zoomOutDisabled} onClick={zoomOut}>
        <Minus aria-hidden="true" size={18} />
      </button>
      <output aria-label="Zoom level">{Math.round(zoom * 100)}%</output>
      <button type="button" aria-label="Zoom in" disabled={zoomInDisabled} onClick={zoomIn}>
        <Plus aria-hidden="true" size={18} />
      </button>
    </div>
  );
}
