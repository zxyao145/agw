import type { NextConfig } from "next";

const backendBaseUrl =
  process.env.BACKEND_API_BASE_URL ??
  process.env.NEXT_PUBLIC_API_BASE_URL ??
  "http://localhost:5015";

const nextConfig: NextConfig = {
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
