import { NextRequest, NextResponse } from "next/server";
import { readdir, stat } from "fs/promises";
import { join, relative, sep } from "path";

export interface FileItem {
  name: string;
  path: string;
  type: "file" | "directory";
  size?: number;
  modifiedTime?: string;
}

export async function GET(request: NextRequest) {
  try {
    const searchParams = request.nextUrl.searchParams;
    const path = searchParams.get("path");

    if (!path) {
      return NextResponse.json(
        { error: "Path parameter is required" },
        { status: 400 }
      );
    }

    // Security: Prevent path traversal attacks
    const normalizedPath = join(path);
    if (normalizedPath.includes("..")) {
      return NextResponse.json(
        { error: "Invalid path" },
        { status: 400 }
      );
    }

    try {
      const entries = await readdir(normalizedPath, { withFileTypes: true });
      const items: FileItem[] = await Promise.all(
        entries.map(async (entry) => {
          const fullPath = join(normalizedPath, entry.name);
          const stats = await stat(fullPath);

          return {
            name: entry.name,
            path: fullPath,
            type: entry.isDirectory() ? "directory" : "file",
            size: entry.isFile() ? stats.size : undefined,
            modifiedTime: stats.mtime.toISOString(),
          };
        })
      );

      // Sort: directories first, then by name
      items.sort((a, b) => {
        if (a.type !== b.type) {
          return a.type === "directory" ? -1 : 1;
        }
        return a.name.localeCompare(b.name);
      });

      return NextResponse.json({ items });
    } catch (err) {
      console.error("Error reading directory:", err);
      return NextResponse.json(
        { error: "Failed to read directory", details: (err as Error).message },
        { status: 500 }
      );
    }
  } catch (err) {
    console.error("Unexpected error:", err);
    return NextResponse.json(
      { error: "Internal server error" },
      { status: 500 }
    );
  }
}
