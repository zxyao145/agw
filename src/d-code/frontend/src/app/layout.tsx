import type { Metadata } from "next";
import { Toaster } from "sonner";

import "./globals.css";

export const metadata: Metadata = {
  title: "D-Code Claude Code Example",
  description: "Standalone Claude Code UI example",
};

export default function RootLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en" suppressHydrationWarning>
      <body className="min-h-screen bg-background text-foreground">
        {children}
        <Toaster
          position="top-center"
          richColors
          closeButton
          style={
            {
              "--toast-close-button-start": "unset",
              "--toast-close-button-end": "0",
              "--toast-close-button-transform": "translate(35%, -35%)",
            } as React.CSSProperties
          }
        />
      </body>
    </html>
  );
}
