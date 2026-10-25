import assert from "node:assert/strict";
import test from "node:test";

import { configureApiRuntime } from "@agw/api";
import { rawApiResponse, startApiServer } from "@agw/test-harness";
import {
  CloudAccessGrants,
  CloudFileSystem,
  type FileWriteLocation,
  formatCloudUri,
  parseCloudUri,
} from "./cloud-file-system";
import { ContextService } from "./context-service";

const PROJECT = "0192f0c5-3c2a-7cc1-9d4e-6f1a2b3c4d5e";
const DIRECTORY = "0192f0c5-aaaa-7cc1-9d4e-6f1a2b3c4d5e";

function fileItem(path: string, type: "file" | "directory" = "file") {
  return {
    directoryId: null,
    name: path.split("/").pop(),
    path,
    type,
    size: type === "file" ? 3 : null,
    modifiedTime: "2026-10-17T08:00:00Z",
    gitStatus: null,
    gitStagedStatus: null,
    gitUnstagedStatus: null,
  };
}

function failure(status: number, code: number, title: string) {
  return rawApiResponse({
    status,
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ code, title }),
  });
}

async function createHarness(routes: Parameters<typeof startApiServer>[0] = {}) {
  const api = await startApiServer(routes);
  const context = new ContextService(
    { version: "0.5.0", platform: "darwin", architecture: "arm64" },
    {
      sidebarVisible: true,
      activeSidebarViewId: "agw.conversations",
      activeMainViewId: "agw.chat",
    },
  );
  const identity = { profileId: "local", baseUrl: api.baseUrl, token: "agw_first" };
  configureApiRuntime({ baseUrl: identity.baseUrl, token: identity.token });
  context.setConnection(identity, { id: "local", name: "Local", kind: "local" });
  const grants = new CloudAccessGrants();
  const written: FileWriteLocation[] = [];
  const files = new CloudFileSystem(context, grants, (location) => written.push(location));
  const uri = (path: string, directoryId: string | null = null) =>
    formatCloudUri({ serverId: "local", projectId: PROJECT, directoryId, path });
  return { api, context, grants, files, uri, identity, written };
}

const reader = {
  kind: "extension" as const,
  extensionId: "sample",
  permissions: ["fs.cloud.read" as const],
};
const writer = {
  kind: "extension" as const,
  extensionId: "sample",
  permissions: ["fs.cloud.read" as const, "fs.cloud.write" as const],
};

test("cloud URIs round-trip with encoded paths and directory keys", () => {
  const uri = formatCloudUri({
    serverId: "local",
    projectId: PROJECT,
    directoryId: DIRECTORY,
    path: "docs/a b#.md",
  });
  assert.equal(uri, `agw-cloud://local/${PROJECT}/${DIRECTORY}/docs/a%20b%23.md`);
  assert.deepEqual(parseCloudUri(uri), {
    serverId: "local",
    projectId: PROJECT,
    directoryId: DIRECTORY,
    path: "docs/a b#.md",
  });
  assert.deepEqual(parseCloudUri(`agw-cloud://local/${PROJECT}/workspace/`), {
    serverId: "local",
    projectId: PROJECT,
    directoryId: null,
    path: "",
  });
  for (const invalid of [
    "agw-cloud://local",
    "agw-cloud:///p/workspace/a",
    "https://local/p/workspace/a",
    "nope",
  ]) {
    assert.throws(() => parseCloudUri(invalid), { code: "ERR_INVALID_URI" }, invalid);
  }
});

test("each operation sends the matching /api/files request", async () => {
  const { api, files, uri } = await createHarness({
    "GET /api/files/list": { items: [fileItem("docs/a.md"), fileItem("docs/sub", "directory")] },
    "GET /api/files/download": rawApiResponse({
      headers: { "content-type": "application/octet-stream" },
      body: new Uint8Array([0, 1, 254, 255]),
    }),
    "GET /api/files/stat": fileItem("docs/a.md"),
    "PUT /api/files/write": fileItem("docs/b.md"),
    "POST /api/files/create-directory": fileItem("docs/archive", "directory"),
    "DELETE /api/files/delete": { success: true, message: "Deleted" },
    "POST /api/files/move": fileItem("docs/archive/a.md"),
  });

  assert.deepEqual(await files.readDirectory(writer, uri("docs")), [
    { name: "a.md", uri: uri("docs/a.md"), type: "file" },
    { name: "sub", uri: uri("docs/sub"), type: "directory" },
  ]);
  assert.deepEqual(
    await files.readFile(writer, uri("assets/logo.png")),
    new Uint8Array([0, 1, 254, 255]),
  );
  assert.deepEqual(await files.stat(writer, uri("docs/a.md")), {
    type: "file",
    size: 3,
    modifiedTime: "2026-10-17T08:00:00Z",
  });
  assert.equal(await files.exists(writer, uri("docs/a.md")), true);
  await files.writeFile(writer, uri("docs/b.md"), new TextEncoder().encode("hello"), {
    overwrite: false,
    createParents: true,
  });
  await files.createDirectory(writer, uri("docs/archive", DIRECTORY), true);
  await files.delete(writer, uri("docs/archive"), false);
  await files.rename(writer, uri("docs/a.md"), uri("docs/archive/a.md"), false);

  const requests = api.requests.filter((request) => request.path.startsWith("/api/files/"));
  const describe = (index: number) => [
    requests[index].method,
    requests[index].path,
    Object.fromEntries(requests[index].query),
  ];
  assert.deepEqual(describe(0), ["GET", "/api/files/list", { projectId: PROJECT, path: "docs" }]);
  assert.deepEqual(describe(1), [
    "GET",
    "/api/files/download",
    { projectId: PROJECT, path: "assets/logo.png" },
  ]);
  assert.deepEqual(describe(2), [
    "GET",
    "/api/files/stat",
    { projectId: PROJECT, path: "docs/a.md" },
  ]);
  assert.deepEqual(describe(4), [
    "PUT",
    "/api/files/write",
    { projectId: PROJECT, path: "docs/b.md", overwrite: "false", createParents: "true" },
  ]);
  assert.match(String(requests[4].body), /hello/u);
  assert.deepEqual(describe(5), [
    "POST",
    "/api/files/create-directory",
    { projectId: PROJECT, path: "docs/archive", recursive: "true", directoryId: DIRECTORY },
  ]);
  assert.deepEqual(describe(6), [
    "DELETE",
    "/api/files/delete",
    { projectId: PROJECT, path: "docs/archive", recursive: "false" },
  ]);
  assert.deepEqual(describe(7), [
    "POST",
    "/api/files/move",
    {
      projectId: PROJECT,
      sourcePath: "docs/a.md",
      destinationPath: "docs/archive/a.md",
      overwrite: "false",
    },
  ]);
});

test("Server error codes become Node.js-style codes", async () => {
  const { files, uri } = await createHarness({
    "DELETE /api/files/delete": failure(409, 4090023, "Directory is not empty."),
    "GET /api/files/stat": failure(404, 4040007, "Resource not found."),
    "GET /api/files/download": failure(413, 4130001, "File exceeds the 100 MiB transfer limit."),
    "PUT /api/files/write": failure(400, 4000040, "File already exists."),
    "POST /api/files/create-directory": failure(403, 4030005, "Outside root."),
    "POST /api/files/move": failure(500, 5000027, "Operation failed."),
  });
  await assert.rejects(files.delete(writer, uri("docs"), false), {
    code: "ENOTEMPTY",
    message: "Directory is not empty.",
  });
  await assert.rejects(files.stat(writer, uri("missing.md")), { code: "ENOENT" });
  assert.equal(await files.exists(writer, uri("missing.md")), false);
  await assert.rejects(files.readFile(writer, uri("big.bin")), { code: "ERR_FILE_TOO_LARGE" });
  await assert.rejects(
    files.writeFile(writer, uri("a.md"), new Uint8Array([1]), {
      overwrite: false,
      createParents: true,
    }),
    { code: "EEXIST" },
  );
  await assert.rejects(files.createDirectory(writer, uri("../x"), true), { code: "EACCES" });
  await assert.rejects(files.rename(writer, uri("a.md"), uri("b.md"), false), {
    code: "ERR_SERVER",
    message: "Operation failed.",
  });
});

test("a read-only target on the Server becomes EACCES like a local write", async () => {
  const { files, uri, written } = await createHarness({
    "PUT /api/files/write": failure(403, 4030007, "'locked.md' is read-only or not writable."),
  });
  await assert.rejects(
    files.writeFile(writer, uri("locked.md"), new Uint8Array([1]), {
      overwrite: true,
      createParents: false,
    }),
    { code: "EACCES", message: "'locked.md' is read-only or not writable." },
  );
  assert.deepEqual(written, []);
});

test("writeFile success calls the file-write listener with the Project, directory and path", async () => {
  const { files, uri, written } = await createHarness({
    "PUT /api/files/write": fileItem("docs/b.md"),
  });
  await files.writeFile(writer, uri("docs/b.md"), new Uint8Array([1]), {
    overwrite: true,
    createParents: false,
  });
  await files.writeFile(writer, uri("notes/a b.md", DIRECTORY), new Uint8Array([2]), {
    overwrite: true,
    createParents: true,
  });
  assert.deepEqual(written, [
    { projectId: PROJECT, directoryId: null, path: "docs/b.md" },
    { projectId: PROJECT, directoryId: DIRECTORY, path: "notes/a b.md" },
  ]);
});

test("expectedSha256 is sent with the write and a changed file becomes ERR_FILE_CHANGED", async () => {
  const expectedSha256 = "0123456789abcdef".repeat(4);
  const { api, files, uri, written } = await createHarness({
    "PUT /api/files/write": (request: { query: URLSearchParams }) =>
      request.query.get("path") === "changed.md"
        ? failure(409, 4090024, "The file was changed after it was read.")
        : fileItem("docs/b.md"),
  });
  await files.writeFile(writer, uri("docs/b.md"), new Uint8Array([1]), {
    overwrite: true,
    createParents: false,
    expectedSha256,
  });
  const sent = api.requests.filter((request) => request.path === "/api/files/write");
  assert.deepEqual(Object.fromEntries(sent[0].query), {
    projectId: PROJECT,
    path: "docs/b.md",
    overwrite: "true",
    createParents: "false",
    expectedSha256,
  });

  await assert.rejects(
    files.writeFile(writer, uri("changed.md"), new Uint8Array([2]), {
      overwrite: true,
      createParents: false,
      expectedSha256,
    }),
    { code: "ERR_FILE_CHANGED", message: "The file was changed after it was read." },
  );
  assert.deepEqual(written, [{ projectId: PROJECT, directoryId: null, path: "docs/b.md" }]);
});

test("a failed or refused writeFile does not call the file-write listener", async () => {
  const { files, uri, written } = await createHarness({
    "PUT /api/files/write": failure(400, 4000040, "File already exists."),
  });
  await assert.rejects(
    files.writeFile(writer, uri("a.md"), new Uint8Array([1]), {
      overwrite: false,
      createParents: true,
    }),
    { code: "EEXIST" },
  );
  await assert.rejects(
    files.writeFile(reader, uri("a.md"), new Uint8Array([1]), {
      overwrite: true,
      createParents: true,
    }),
    { code: "ERR_PERMISSION_DENIED" },
  );
  assert.deepEqual(written, []);
});

test("the host reads and writes without extension permissions or grants, only on the current Server", async () => {
  const { api, files, uri, written } = await createHarness({
    "GET /api/files/download": rawApiResponse({
      headers: { "content-type": "application/octet-stream" },
      body: "text",
    }),
    "PUT /api/files/write": fileItem("src/app.ts"),
  });
  const host = { kind: "host" as const };
  assert.deepEqual(await files.readFile(host, uri("src/app.ts")), new TextEncoder().encode("text"));
  await files.writeFile(host, uri("src/app.ts"), new Uint8Array([1]), {
    overwrite: true,
    createParents: false,
    expectedSha256: "0123456789abcdef".repeat(4),
  });
  assert.deepEqual(written, [{ projectId: PROJECT, directoryId: null, path: "src/app.ts" }]);
  const sent = api.requests.length;
  await assert.rejects(
    files.readFile(
      host,
      formatCloudUri({ serverId: "remote-9", projectId: PROJECT, directoryId: null, path: "a.md" }),
    ),
    { code: "ERR_CONNECTION_CHANGED" },
  );
  assert.equal(api.requests.length, sent);
});

test("URIs for another server, another directory, or missing permissions are refused before sending", async () => {
  const { api, files, uri } = await createHarness();
  const sent = api.requests.length;
  await assert.rejects(
    files.readFile(
      writer,
      formatCloudUri({ serverId: "remote-9", projectId: PROJECT, directoryId: null, path: "a.md" }),
    ),
    { code: "ERR_CONNECTION_CHANGED" },
  );
  await assert.rejects(files.rename(writer, uri("a.md"), uri("a.md", DIRECTORY), false), {
    code: "ERR_CROSS_DEVICE",
  });
  await assert.rejects(
    files.readFile({ kind: "extension", extensionId: "sample", permissions: [] }, uri("a.md")),
    { code: "ERR_PERMISSION_DENIED" },
  );
  await assert.rejects(files.delete(reader, uri("a.md"), false), { code: "ERR_PERMISSION_DENIED" });
  assert.equal(api.requests.length, sent);
});

test("context-menu grants allow reading until the connection identity changes or the extension stops", async () => {
  const { api, context, grants, files, uri, identity } = await createHarness({
    "GET /api/files/download": rawApiResponse({
      headers: { "content-type": "application/octet-stream" },
      body: "ok",
    }),
  });
  const none = { kind: "extension" as const, extensionId: "viewer", permissions: [] };
  grants.grant("viewer", uri("docs"), true, context.connectionKey!);
  assert.deepEqual(
    await files.readFile(none, uri("docs/nested/a.md")),
    new TextEncoder().encode("ok"),
  );
  await assert.rejects(files.readFile(none, uri("other.md")), { code: "ERR_PERMISSION_DENIED" });

  for (const change of [
    { baseUrl: identity.baseUrl.replace("127.0.0.1", "localhost") },
    { token: "agw_second" },
  ]) {
    const next = { ...identity, ...change };
    grants.grant("viewer", uri("docs"), true, context.connectionKey!);
    configureApiRuntime({ baseUrl: next.baseUrl, token: next.token });
    context.setConnection(next, { id: "local", name: "Local", kind: "local" });
    const sent = api.requests.length;
    await assert.rejects(files.readFile(none, uri("docs/a.md")), { code: "ERR_PERMISSION_DENIED" });
    assert.equal(api.requests.length, sent);
  }

  grants.grant("viewer", uri("docs/a.md"), false, context.connectionKey!);
  assert.deepEqual(await files.readFile(none, uri("docs/a.md")), new TextEncoder().encode("ok"));
  grants.release("viewer");
  await assert.rejects(files.readFile(none, uri("docs/a.md")), { code: "ERR_PERMISSION_DENIED" });
});
