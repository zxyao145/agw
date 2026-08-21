import assert from "node:assert/strict";
import test from "node:test";

import { QueryClient } from "@agw/components/query";
import type { ServerProfile } from "@desktop/shared/contracts";
import { ServerQueryClientRegistry } from "./query-client-registry";

function profile(id: string, baseUrl = `https://${id}.example.com`): ServerProfile {
  return {
    id,
    kind: "remote",
    name: id,
    baseUrl,
    apiMajorVersion: 1,
    allowInsecureHttp: false,
  };
}

function createRegistry(): ServerQueryClientRegistry {
  return new ServerQueryClientRegistry(
    () =>
      new QueryClient({
        defaultOptions: { queries: { retry: false } },
      }),
  );
}

test("same profile identity restores its existing cache", () => {
  const registry = createRegistry();
  const serverA = profile("server-a");
  const first = registry.get(serverA, "token-a");
  first.setQueryData(["projects"], ["cached-a"]);

  const restored = registry.get({ ...serverA, baseUrl: `${serverA.baseUrl}/` }, "token-a");

  assert.equal(restored, first);
  assert.deepEqual(restored.getQueryData(["projects"]), ["cached-a"]);
});

test("profiles remain isolated and switching back restores the original cache", () => {
  const registry = createRegistry();
  const serverA = profile("server-a");
  const serverB = profile("server-b");
  const clientA = registry.get(serverA, "token-a");
  clientA.setQueryData(["projects"], ["cached-a"]);
  const clientB = registry.get(serverB, "token-b");

  assert.notEqual(clientB, clientA);
  assert.equal(clientB.getQueryData(["projects"]), undefined);
  assert.equal(registry.get(serverA, "token-a"), clientA);
  assert.deepEqual(clientA.getQueryData(["projects"]), ["cached-a"]);
});

test("token or base URL changes clear and replace that profile client", () => {
  const registry = createRegistry();
  const server = profile("server-a");
  const tokenClient = registry.get(server, "token-a");
  tokenClient.setQueryData(["secret"], "old-token-data");

  const rotatedClient = registry.get(server, "token-b");
  assert.notEqual(rotatedClient, tokenClient);
  assert.equal(tokenClient.getQueryData(["secret"]), undefined);

  rotatedClient.setQueryData(["projects"], ["old-url-data"]);
  const movedClient = registry.get({ ...server, baseUrl: "https://new.example.com/" }, "token-b");
  assert.notEqual(movedClient, rotatedClient);
  assert.equal(rotatedClient.getQueryData(["projects"]), undefined);
});

test("prune evicts deleted profiles without touching retained profiles", () => {
  const registry = createRegistry();
  const serverA = profile("server-a");
  const serverB = profile("server-b");
  const clientA = registry.get(serverA, "token-a");
  const clientB = registry.get(serverB, "token-b");
  clientA.setQueryData(["projects"], ["a"]);
  clientB.setQueryData(["projects"], ["b"]);

  registry.prune(["server-a"]);

  assert.deepEqual(clientA.getQueryData(["projects"]), ["a"]);
  assert.equal(clientB.getQueryData(["projects"]), undefined);
  assert.notEqual(registry.get(serverB, "token-b"), clientB);
});
