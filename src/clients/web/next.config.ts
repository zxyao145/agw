import { dirname, resolve } from "node:path";
import { codeInspectorPlugin } from "code-inspector-plugin";
import type { NextConfig } from "next";

const backendBaseUrl =
  process.env.BACKEND_API_BASE_URL ??
  process.env.NEXT_PUBLIC_API_BASE_URL ??
  "http://localhost:30816";

const outputMode = process.env.NEXT_OUTPUT_MODE;
const isStaticExport = outputMode === "export";
const webRoot = dirname(require.resolve("./package.json"));
const clientsRoot = resolve(webRoot, "..");

const nextConfig: NextConfig = {
  allowedDevOrigins: ["agw.local"],
  transpilePackages: [
    "@agw/agents",
    "@agw/api",
    "@agw/auth",
    "@agw/chat",
    "@agw/components",
    "@agw/integrations",
    "@agw/jobs",
    "@agw/observability",
    "@agw/projects",
    "@agw/providers",
    "@agw/settings",
    "@agw/skills",
  ],
  output: outputMode === "export" || outputMode === "standalone" ? outputMode : undefined,
  trailingSlash: isStaticExport,
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

    return config;
  },
};

if (!isStaticExport) {
  nextConfig.rewrites = async () => [
    // Proxy backend APIs to avoid CORS in local dev and local app mode.
    { source: "/api/:path*", destination: `${backendBaseUrl}/api/:path*` },
    // OpenAPI endpoint (Development): /openapi
    { source: "/openapi/:path*", destination: `${backendBaseUrl}/openapi/:path*` },
  ];
}

export default nextConfig;
