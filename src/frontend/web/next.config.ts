import type { NextConfig } from "next";
const { codeInspectorPlugin } = require('code-inspector-plugin');

const backendBaseUrl =
  process.env.BACKEND_API_BASE_URL ??
  process.env.NEXT_PUBLIC_API_BASE_URL ??
  "http://localhost:5015";

const nextConfig: NextConfig = {
  turbopack: {
    rules: codeInspectorPlugin({
      bundler: 'turbopack',
    }),
  },
  async rewrites() {
    return [
      // Proxy backend APIs to avoid CORS in local dev.
      { source: "/api/:path*", destination: `${backendBaseUrl}/api/:path*` },
      // OpenAPI endpoint (Development): /openapi
      { source: "/openapi/:path*", destination: `${backendBaseUrl}/openapi/:path*` },
    ];
  },
};

export default nextConfig;
