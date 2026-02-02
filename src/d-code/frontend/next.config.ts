import type { NextConfig } from "next";

const backendBaseUrl =
  process.env.DCODE_BACKEND_BASE_URL ??
  process.env.NEXT_PUBLIC_API_BASE_URL ??
  "http://localhost:5015";

const nextConfig: NextConfig = {
  async rewrites() {
    return [
      { source: "/api/:path*", destination: `${backendBaseUrl}/api/:path*` },
    ];
  },
};

export default nextConfig;
