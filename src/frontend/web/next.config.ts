import type { NextConfig } from "next";
const { codeInspectorPlugin } = require("code-inspector-plugin");

const backendBaseUrl =
  process.env.BACKEND_API_BASE_URL ??
  process.env.NEXT_PUBLIC_API_BASE_URL ??
  "http://localhost:5015";

const outputMode = process.env.NEXT_OUTPUT_MODE;
const isStaticExport = outputMode === "export";

const nextConfig: NextConfig = {
  output: outputMode === "export" || outputMode === "standalone" ? outputMode : undefined,
  trailingSlash: isStaticExport,
  turbopack: {
    rules: codeInspectorPlugin({
      bundler: "turbopack",
    }),
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
