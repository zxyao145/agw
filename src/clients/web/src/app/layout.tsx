import type { Metadata } from "next";
import { QueryProvider } from "@agw/components";
import { ThemeProvider } from "@agw/components";
import { Toaster } from "@agw/components";
import { TooltipProvider } from "@agw/components";

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
            </QueryProvider>
          </TooltipProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}
