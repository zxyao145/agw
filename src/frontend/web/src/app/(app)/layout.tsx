"use client"

import * as React from "react"
import Link from "next/link"
import { usePathname } from "next/navigation"
import { MenuIcon } from "lucide-react"

import { cn } from "@/lib/utils"
import { Button } from "@/components/ui/button"
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb"
import { Separator } from "@/components/ui/separator"
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
  SheetTrigger,
} from "@/components/ui/sheet"
import { QueryErrorBoundary } from "@/components/query-error-boundary"

const navItems = [
  { href: "/projects", label: "Projects" },
  { href: "/workflows", label: "Workflows" },
  { href: "/agents", label: "Agents" },
  { href: "/providers", label: "Providers" },
  { href: "/models", label: "Models" },
  { href: "/model-providers", label: "Model Providers" },
] as const

function getActiveNavLabel(pathname: string): string | undefined {
  const match = navItems.find((x) => pathname === x.href || pathname.startsWith(`${x.href}/`))
  return match?.label
}

export default function AppLayout({ children }: { children: React.ReactNode }) {
  const pathname = usePathname()
  const activeLabel = getActiveNavLabel(pathname)

  return (
    <div className="min-h-screen bg-background text-foreground">
      <div className="grid min-h-screen grid-cols-1 lg:grid-cols-[260px_1fr]">
        <aside className="hidden border-r bg-card lg:block">
          <div className="flex h-16 items-center border-b px-6">
            <Link href="/projects" className="font-semibold tracking-tight">
              DSystem Admin
            </Link>
          </div>
          <nav className="p-3">
            <ul className="space-y-1">
              {navItems.map((item) => {
                const isActive = pathname === item.href || pathname.startsWith(`${item.href}/`)
                return (
                  <li key={item.href}>
                    <Link
                      href={item.href}
                      className={cn(
                        "block rounded-md px-3 py-2 text-sm transition-colors",
                        isActive
                          ? "bg-accent text-accent-foreground"
                          : "text-muted-foreground hover:bg-accent hover:text-accent-foreground"
                      )}
                    >
                      {item.label}
                    </Link>
                  </li>
                )
              })}
            </ul>
          </nav>
        </aside>

        <div className="min-w-0">
          <header className="sticky top-0 z-40 flex h-16 items-center gap-3 border-b bg-background/80 px-4 backdrop-blur supports-[backdrop-filter]:bg-background/60 lg:px-6">
            <div className="flex items-center gap-3">
              <Sheet>
                <SheetTrigger asChild>
                  <Button variant="outline" size="icon" className="lg:hidden">
                    <MenuIcon className="size-4" />
                    <span className="sr-only">Open navigation</span>
                  </Button>
                </SheetTrigger>
                <SheetContent side="left" className="w-[280px] p-0">
                  <SheetHeader className="border-b p-4">
                    <SheetTitle>DSystem Admin</SheetTitle>
                    <SheetDescription className="sr-only">
                      Application navigation
                    </SheetDescription>
                  </SheetHeader>
                  <nav className="p-3">
                    <ul className="space-y-1">
                      {navItems.map((item) => {
                        const isActive =
                          pathname === item.href || pathname.startsWith(`${item.href}/`)
                        return (
                          <li key={item.href}>
                            <Link
                              href={item.href}
                              className={cn(
                                "block rounded-md px-3 py-2 text-sm transition-colors",
                                isActive
                                  ? "bg-accent text-accent-foreground"
                                  : "text-muted-foreground hover:bg-accent hover:text-accent-foreground"
                              )}
                            >
                              {item.label}
                            </Link>
                          </li>
                        )
                      })}
                    </ul>
                  </nav>
                </SheetContent>
              </Sheet>

              <div className="hidden h-6 items-center gap-3 lg:flex">
                <div className="text-sm font-medium">DSystem Admin</div>
                <Separator orientation="vertical" />
                <div className="text-sm text-muted-foreground">Backend Management Console</div>
              </div>
            </div>

            <div className="min-w-0 flex-1">
              <Breadcrumb>
                <BreadcrumbList>
                  <BreadcrumbItem>
                    <BreadcrumbLink asChild>
                      <Link href="/projects">Home</Link>
                    </BreadcrumbLink>
                  </BreadcrumbItem>
                  {activeLabel ? (
                    <>
                      <BreadcrumbSeparator />
                      <BreadcrumbItem>
                        <BreadcrumbPage>{activeLabel}</BreadcrumbPage>
                      </BreadcrumbItem>
                    </>
                  ) : null}
                </BreadcrumbList>
              </Breadcrumb>
            </div>
          </header>

          <main className="min-w-0 p-4 lg:p-6">
            <QueryErrorBoundary>{children}</QueryErrorBoundary>
          </main>
        </div>
      </div>
    </div>
  )
}