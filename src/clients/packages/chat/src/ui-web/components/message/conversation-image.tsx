"use client";

import * as React from "react";
import { cn } from "@agw/components";
import { toast } from "sonner";

export type ConversationImageSource = {
  src: string;
  alt: string;
  name?: string;
  width?: number;
  height?: number;
};

export type ImageContextMenuRequest = {
  src: string;
  name?: string;
  x: number;
  y: number;
  canCopy: boolean;
};

type ImageContextMenuHandler = (request: ImageContextMenuRequest) => Promise<void>;

const ImagePlatformContext = React.createContext<ImageContextMenuHandler | undefined>(undefined);

export function ConversationImagePlatformProvider({
  onImageContextMenu,
  children,
}: React.PropsWithChildren<{ onImageContextMenu?: ImageContextMenuHandler }>) {
  return (
    <ImagePlatformContext.Provider value={onImageContextMenu}>
      {children}
    </ImagePlatformContext.Provider>
  );
}

export const ConversationImageContext = React.createContext<
  ((image: ConversationImageSource, trigger: HTMLImageElement) => void) | null
>(null);

export function useImageContextMenu(name?: string) {
  const showMenu = React.useContext(ImagePlatformContext);
  return (event: React.MouseEvent<HTMLImageElement>) => {
    if (!showMenu) return;
    event.preventDefault();
    event.stopPropagation();
    const image = event.currentTarget;
    void showMenu({
      src: image.currentSrc || image.src,
      name,
      x: Math.round(event.clientX),
      y: Math.round(event.clientY),
      canCopy: image.complete && image.naturalWidth > 0,
    }).catch((error: unknown) => {
      console.error("Unable to complete image action", error);
      toast.error(error instanceof Error ? error.message : "Unable to complete image action");
    });
  };
}

export function ConversationImage({
  src,
  alt,
  name,
  title,
  className,
}: ConversationImageSource & { title?: string; className?: string }) {
  const openImage = React.useContext(ConversationImageContext);
  const onContextMenu = useImageContextMenu(name);
  const [failedSource, setFailedSource] = React.useState<string | null>(null);
  const open = (image: HTMLImageElement) => {
    image.focus({ preventScroll: true });
    openImage?.(
      {
        src: image.currentSrc || image.src,
        alt,
        name,
        width: image.naturalWidth || undefined,
        height: image.naturalHeight || undefined,
      },
      image,
    );
  };

  return (
    <>
      <img
        src={src}
        alt={alt}
        title={title}
        className={cn(
          "inline-block max-h-70 max-w-full rounded-lg border bg-muted object-contain shadow-xs",
          openImage && "cursor-zoom-in focus-visible:outline-2 focus-visible:outline-ring",
          className,
        )}
        loading="lazy"
        decoding="async"
        role={openImage ? "button" : undefined}
        tabIndex={openImage ? 0 : undefined}
        aria-label={openImage ? `Preview image: ${alt}` : undefined}
        aria-haspopup={openImage ? "dialog" : undefined}
        onClick={(event) => {
          if (!openImage) return;
          event.preventDefault();
          event.stopPropagation();
          open(event.currentTarget);
        }}
        onKeyDown={(event) => {
          if (!openImage || (event.key !== "Enter" && event.key !== " ")) return;
          event.preventDefault();
          event.stopPropagation();
          open(event.currentTarget);
        }}
        onContextMenu={onContextMenu}
        onError={() => setFailedSource(src)}
        onLoad={() => setFailedSource(null)}
      />
      {failedSource === src ? (
        <span role="status" className="block text-sm text-destructive">
          Unable to load image: {alt}
        </span>
      ) : null}
    </>
  );
}
