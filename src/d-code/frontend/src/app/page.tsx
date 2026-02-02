import Link from "next/link";

export default function HomePage() {
  return (
    <main className="mx-auto flex min-h-screen max-w-3xl flex-col gap-6 p-10">
      <h1 className="text-3xl font-semibold">D-Code Example</h1>
      <p className="text-muted-foreground">
        This standalone example hosts the Claude Code UI extracted from the main
        D-System frontend.
      </p>
      <Link
        className="inline-flex w-fit items-center rounded-md border px-4 py-2 text-sm font-medium hover:bg-accent"
        href="/claude-code"
      >
        Open Claude Code
      </Link>
    </main>
  );
}
