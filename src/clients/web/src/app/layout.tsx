import type { Metadata } from "next";
import { QueryProvider } from "@/components/query-provider";
import { ThemeProvider } from "@/components/theme-provider";
import { Toaster } from "@/components/ui/sonner";
import { TooltipProvider } from "@/components/ui/tooltip";
import { DesktopRuntimeProvider } from "@/lib/desktop-runtime";

import "./globals.css";

export const metadata: Metadata = {
  title: "Agw",
  description: "Agent Gateway, base MAF",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
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
            <QueryProvider>
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
            </QueryProvider>
          </TooltipProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}
