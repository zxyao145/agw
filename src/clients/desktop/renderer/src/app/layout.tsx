import type { Metadata } from "next";

import { ThemeProvider, Toaster, TooltipProvider } from "@agw/components";
import { DesktopRuntimeProvider } from "@/runtime";

import "./globals.css";

export const metadata: Metadata = {
  title: "Agw Desktop",
  description: "Agw Desktop client",
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en" suppressHydrationWarning>
      <body className="antialiased">
        <ThemeProvider
          attribute="class"
          defaultTheme="system"
          enableSystem
          disableTransitionOnChange
        >
          <TooltipProvider>
            <DesktopRuntimeProvider>
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
            </DesktopRuntimeProvider>
          </TooltipProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}
