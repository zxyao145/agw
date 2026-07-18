import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { codeInspectorPlugin } from "code-inspector-plugin";
import type { NextConfig } from "next";

const rendererRoot = dirname(fileURLToPath(import.meta.url));
const desktopRoot = resolve(rendererRoot, "..");
const clientsRoot = resolve(rendererRoot, "..", "..");

const nextConfig: NextConfig = {
  output: "export",
  trailingSlash: true,
  transpilePackages: [
    "@agw/agents",
    "@agw/api",
    "@agw/chat",
    "@agw/components",
    "@agw/integrations",
    "@agw/jobs",
    "@agw/observability",
    "@agw/projects",
    "@agw/providers",
    "@agw/skills",
  ],
  turbopack: {
    root: clientsRoot,
  },
  webpack(config, { dev }) {
    if (dev) {
      config.plugins.push(
        codeInspectorPlugin({
          bundler: "webpack",
          lang: "zh",
        }),
      );
    }

    config.resolve.alias = {
      ...config.resolve.alias,
      "next-themes": resolve(desktopRoot, "node_modules", "next-themes"),
      sonner: resolve(desktopRoot, "node_modules", "sonner"),
    };
    return config;
  },
};

export default nextConfig;
