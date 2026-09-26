"use client";

import * as React from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { XIcon } from "lucide-react";
import { Dialog as DialogPrimitive } from "radix-ui";

import { cn } from "../../lib/cn";

const dialogVariants = cva(
  "bg-popover text-popover-foreground data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95 fixed top-[50%] left-[50%] z-50 grid w-full max-w-[calc(100%-2rem)] translate-x-[-50%] translate-y-[-50%] gap-4 border border-transparent p-6 shadow-[0_16px_48px_-8px_rgb(0_0_0/0.24),0_4px_12px_-4px_rgb(0_0_0/0.12)] duration-200 outline-none dark:border-border",
  {
    variants: {
      size: {
        sm: "sm:max-w-[var(--dialog-max-w-sm)]" /* 384px - Simple confirmations */,
        md: "sm:max-w-[var(--dialog-max-w-md)]" /* 512px - Default, simple forms */,
        lg: "sm:max-w-[var(--dialog-max-w-lg)]" /* 672px - Medium forms */,
        xl: "sm:max-w-[var(--dialog-max-w-xl)]" /* 768px - Complex forms */,
        "2xl": "sm:max-w-[var(--dialog-max-w-2xl)]" /* 896px - Wide content */,
        "3xl": "sm:max-w-[var(--dialog-max-w-3xl)]" /* 1152px - Very wide content */,
        full: "sm:max-w-[calc(100vw-4rem)] sm:max-h-[calc(100vh-4rem)]" /* Full viewport with padding */,
        fullscreen:
          "top-0 left-0 flex h-screen max-h-none w-screen max-w-none translate-x-0 translate-y-0 flex-col rounded-none border-0 bg-background p-4 text-foreground shadow-none sm:max-w-none",
      },
    },
    defaultVariants: {
      size: "md",
    },
  },
);

function Dialog({ ...props }: React.ComponentProps<typeof DialogPrimitive.Root>) {
  return <DialogPrimitive.Root data-slot="dialog" {...props} />;
}

function DialogTrigger({ ...props }: React.ComponentProps<typeof DialogPrimitive.Trigger>) {
  return <DialogPrimitive.Trigger data-slot="dialog-trigger" {...props} />;
}

function DialogPortal({ ...props }: React.ComponentProps<typeof DialogPrimitive.Portal>) {
  return <DialogPrimitive.Portal data-slot="dialog-portal" {...props} />;
}

function DialogClose({ ...props }: React.ComponentProps<typeof DialogPrimitive.Close>) {
  return <DialogPrimitive.Close data-slot="dialog-close" {...props} />;
}

function DialogOverlay({
  className,
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Overlay>) {
  return (
    <DialogPrimitive.Overlay
      data-slot="dialog-overlay"
      className={cn(
        "data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 fixed inset-0 z-50 bg-black/50",
        className,
      )}
      {...props}
    />
  );
}

interface DialogContentProps
  extends
    React.ComponentProps<typeof DialogPrimitive.Content>,
    VariantProps<typeof dialogVariants> {
  showCloseButton?: boolean;
}

function DialogContent({
  className,
  children,
  showCloseButton = true,
  size,
  ...props
}: DialogContentProps) {
  return (
    <DialogPortal data-slot="dialog-portal">
      <DialogOverlay />
      <DialogPrimitive.Content
        data-slot="dialog-content"
        data-size={size}
        className={cn(
          "rounded-2xl",
          dialogVariants({ size }),
          showCloseButton && "[&>[data-slot=dialog-header]]:pr-8",
          className,
        )}
        {...props}
      >
        {children}
        {showCloseButton && (
          <DialogPrimitive.Close
            data-slot="dialog-close"
            className="ring-offset-popover focus-visible:ring-ring absolute top-3 right-3 flex size-8 items-center justify-center rounded-lg bg-muted text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:outline-hidden disabled:pointer-events-none [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4"
          >
            <XIcon />
            <span className="sr-only">Close</span>
          </DialogPrimitive.Close>
        )}
      </DialogPrimitive.Content>
    </DialogPortal>
  );
}

function DialogHeader({ className, ...props }: React.ComponentProps<"div">) {
  return (
    <div
      data-slot="dialog-header"
      className={cn("flex flex-col gap-2 text-center sm:text-left", className)}
      {...props}
    />
  );
}

function DialogFooter({ className, ...props }: React.ComponentProps<"div">) {
  return (
    <div
      data-slot="dialog-footer"
      className={cn("flex flex-col-reverse gap-2 sm:flex-row sm:justify-end", className)}
      {...props}
    />
  );
}

function DialogTitle({ className, ...props }: React.ComponentProps<typeof DialogPrimitive.Title>) {
  return (
    <DialogPrimitive.Title
      data-slot="dialog-title"
      className={cn("text-lg leading-none font-semibold", className)}
      {...props}
    />
  );
}

function DialogDescription({
  className,
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Description>) {
  return (
    <DialogPrimitive.Description
      data-slot="dialog-description"
      className={cn("text-muted-foreground text-sm", className)}
      {...props}
    />
  );
}

export {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogOverlay,
  DialogPortal,
  DialogTitle,
  DialogTrigger,
  dialogVariants,
};
