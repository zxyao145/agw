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
    // data-agw-document 让 CSS 主题限定只作用于宿主的规则。data-agw-document lets CSS themes scope rules to the host.
    <html lang="en" suppressHydrationWarning data-agw-document="desktop">
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
