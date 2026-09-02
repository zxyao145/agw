"use client";

import * as React from "react";

import type { UserInputMarker } from "./user-input-navigation";

const PREVIEW_ESTIMATED_HEIGHT = 96;

type PreviewState = {
  key: string;
  top: number;
};

export type UserInputNavigatorProps = {
  markers: readonly UserInputMarker[];
  activeKey: string | null;
  height: number;
  onSelect: (rowIndex: number) => void;
};

export function UserInputNavigator({
  markers,
  activeKey,
  height,
  onSelect,
}: UserInputNavigatorProps) {
  const navigationRef = React.useRef<HTMLElement>(null);
  const [preview, setPreview] = React.useState<PreviewState | null>(null);
  const previewMarker = markers.find((marker) => marker.key === preview?.key) ?? null;

  const showPreview = (marker: UserInputMarker, button: HTMLButtonElement) => {
    const navigation = navigationRef.current;
    if (!navigation) return;

    const navigationRect = navigation.getBoundingClientRect();
    const buttonRect = button.getBoundingClientRect();
    const desiredTop =
      buttonRect.top + buttonRect.height / 2 - navigationRect.top - PREVIEW_ESTIMATED_HEIGHT / 2;
    setPreview({
      key: marker.key,
      top: Math.min(Math.max(desiredTop, 0), Math.max(height - PREVIEW_ESTIMATED_HEIGHT, 0)),
    });
  };

  const handleScroll = (event: React.UIEvent<HTMLDivElement>) => {
    const focusedElement = document.activeElement;
    if (
      focusedElement instanceof HTMLElement &&
      focusedElement.tagName === "BUTTON" &&
      event.currentTarget.contains(focusedElement)
    ) {
      const focusedButton = focusedElement as HTMLButtonElement;
      const focusedMarker = markers.find(
        (marker) => marker.key === focusedButton.dataset.markerKey,
      );
      if (focusedMarker) {
        showPreview(focusedMarker, focusedButton);
        return;
      }
    }

    setPreview(null);
  };

  return (
    <nav
      ref={navigationRef}
      aria-label="User input navigation"
      className="pointer-events-auto relative w-6"
      style={{ height }}
    >
      <div
        className="absolute inset-0 overflow-y-auto [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
        onScroll={handleScroll}
      >
        <div className="flex min-h-full w-6 flex-col justify-center">
          {markers.map((marker) => {
            const isActive = marker.key === activeKey;
            const isPreviewed = marker.key === preview?.key;
            return (
              <button
                key={marker.key}
                type="button"
                data-marker-key={marker.key}
                aria-current={isActive ? "location" : undefined}
                aria-describedby={isPreviewed ? "user-input-navigation-preview" : undefined}
                aria-label={`Jump to user input: ${marker.preview}`}
                className="flex h-6 w-6 shrink-0 cursor-pointer items-center rounded-sm pl-2 outline-none focus-visible:ring-2 focus-visible:ring-ring/70"
                onClick={() => onSelect(marker.rowIndex)}
                onMouseEnter={(event) => showPreview(marker, event.currentTarget)}
                onMouseLeave={() =>
                  setPreview((current) => (current?.key === marker.key ? null : current))
                }
                onFocus={(event) => showPreview(marker, event.currentTarget)}
                onBlur={() =>
                  setPreview((current) => (current?.key === marker.key ? null : current))
                }
              >
                <span
                  aria-hidden="true"
                  className={
                    isActive
                      ? "block h-0.5 w-2 origin-left bg-foreground transition-[width,background-color] duration-150"
                      : isPreviewed
                        ? "block h-0.5 w-4 origin-left bg-foreground/80 transition-[width,background-color] duration-150"
                        : "block h-0.5 w-2 origin-left bg-muted-foreground/45 transition-[width,background-color] duration-150"
                  }
                />
              </button>
            );
          })}
        </div>
      </div>

      {previewMarker && preview ? (
        <div
          id="user-input-navigation-preview"
          role="tooltip"
          className="pointer-events-none absolute left-8 z-30 w-80 max-w-[calc(100cqw-4rem)] rounded-md border bg-popover px-2 py-2 text-left text-popover-foreground shadow-xl"
          style={{ top: preview.top }}
        >
          <p className="line-clamp-3 whitespace-normal wrap-break-word text-xs text-muted-foreground">
            {previewMarker.preview}
          </p>
        </div>
      ) : null}
    </nav>
  );
}
