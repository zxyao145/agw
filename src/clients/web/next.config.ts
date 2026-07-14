// import type { NextConfig } from "next";
// const { codeInspectorPlugin } = require("code-inspector-plugin");

// const backendBaseUrl =
//   process.env.BACKEND_API_BASE_URL ??
//   process.env.NEXT_PUBLIC_API_BASE_URL ??
//   "http://localhost:5015";

// const outputMode = process.env.NEXT_OUTPUT_MODE;
// const isStaticExport = outputMode === "export";

// const nextConfig: NextConfig = {
//   output: outputMode === "export" || outputMode === "standalone" ? outputMode : undefined,
//   trailingSlash: isStaticExport,
//   turbopack: {
//     rules: codeInspectorPlugin({
//       bundler: "turbopack",
//     }),
//   },
// };

// if (!isStaticExport) {
//   nextConfig.rewrites = async () => [
//     // Proxy backend APIs to avoid CORS in local dev and local app mode.
//     { source: "/api/:path*", destination: `${backendBaseUrl}/api/:path*` },
//     // OpenAPI endpoint (Development): /openapi
//     { source: "/openapi/:path*", destination: `${backendBaseUrl}/openapi/:path*` },
//   ];
// }

// export default nextConfig;

import { dirname } from "node:path";
import type { Server } from "node:http";
import {
  createServer,
  setProjectRecord,
  type CodeOptions,
  type RecordInfo,
} from "@code-inspector/core";
import type { NextConfig } from "next";
import { PHASE_DEVELOPMENT_SERVER } from "next/constants";

const { codeInspectorPlugin } = require("code-inspector-plugin");

const backendBaseUrl =
  process.env.BACKEND_API_BASE_URL ??
  process.env.NEXT_PUBLIC_API_BASE_URL ??
  "http://localhost:5015";

const outputMode = process.env.NEXT_OUTPUT_MODE;
const isStaticExport = outputMode === "export";
const codeInspectorOutput = dirname(require.resolve("code-inspector-plugin"));
const codeInspectorOptions = {
  bundler: "turbopack",
  server: "close",
} satisfies CodeOptions;

type CodeInspectorState = {
  server?: Server;
  port?: number;
  starting?: Promise<number>;
};

const globalForCodeInspector = globalThis as typeof globalThis & {
  __agwCodeInspector?: CodeInspectorState;
};
const codeInspectorState =
  globalForCodeInspector.__agwCodeInspector ?? (globalForCodeInspector.__agwCodeInspector = {});

function createCodeInspectorRecord(): RecordInfo {
  return { port: 0, entry: "", output: codeInspectorOutput };
}

function startCodeInspectorServer(): Promise<number> {
  return new Promise((resolve, reject) => {
    const start = () => {
      let server: Server;
      const handleError = (error: NodeJS.ErrnoException) => {
        if (error.code === "EADDRINUSE") {
          start();
          return;
        }
        reject(error);
      };

      server = createServer(
        (port) => {
          server.off("error", handleError);
          codeInspectorState.server = server;
          codeInspectorState.port = port;
          server.once("close", () => {
            if (codeInspectorState.server === server) {
              codeInspectorState.server = undefined;
              codeInspectorState.port = undefined;
              codeInspectorState.starting = undefined;
            }
          });
          resolve(port);
        },
        { ...codeInspectorOptions, server: "open" },
        createCodeInspectorRecord(),
      );
      server.once("error", handleError);
    };

    start();
  });
}

async function ensureCodeInspectorServer(): Promise<void> {
  if (!codeInspectorState.server?.listening) {
    codeInspectorState.starting ??= startCodeInspectorServer().catch((error) => {
      codeInspectorState.starting = undefined;
      throw error;
    });
  }

  const port = codeInspectorState.port ?? (await codeInspectorState.starting);
  setProjectRecord(createCodeInspectorRecord(), "port", port);
}

export default async function getNextConfig(phase: string): Promise<NextConfig> {
  const inspectorRules = codeInspectorPlugin(codeInspectorOptions);

  if (phase === PHASE_DEVELOPMENT_SERVER) {
    await ensureCodeInspectorServer();
  }

  const nextConfig: NextConfig = {
    output: outputMode === "export" || outputMode === "standalone" ? outputMode : undefined,
    trailingSlash: isStaticExport,
    turbopack: {
      rules: inspectorRules,
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

  return nextConfig;
}
