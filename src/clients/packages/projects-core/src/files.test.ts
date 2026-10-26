import assert from "node:assert/strict";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import test from "node:test";

import { configureApiRuntime, createBearerApiClient, resetApiRuntime } from "@agw/api";
import {
  createProjectFilesService,
  deleteFile,
  downloadFile,
  FileApiError,
  writeFile,
  type FileItem,
} from "./files";

type RecordedRequest = {
  method: string;
  path: string;
  query: URLSearchParams;
  headers: IncomingMessage["headers"];
  body: Buffer;
};

type Reply = { status?: number; contentType?: string; body: string | Uint8Array };

type Handler = (request: RecordedRequest) => Reply | Promise<Reply>;

const item = (path: string, type: FileItem["type"] = "file"): FileItem => ({
  directoryId: null,
  name: path.split("/").pop() ?? "",
  path,
  type,
  size: type === "file" ? 4 : undefined,
  modifiedTime: "2026-10-17T08:00:00Z",
});

const ok = (data: unknown): Reply => ({ body: JSON.stringify({ code: 0, title: "OK", data }) });

const fail = (status: number, code: number, title: string): Reply => ({
  status,
  body: JSON.stringify({ code, title }),
});

/**
 * Serves the handler over real HTTP so the requests go through the actual fetch and multipart path.
 * 通过真实 HTTP 提供处理函数，请求走实际的 fetch 与 multipart 路径。
 */
async function startServer(handler: Handler) {
  const requests: RecordedRequest[] = [];
  const server = createServer((incoming: IncomingMessage, response: ServerResponse) => {
    const chunks: Buffer[] = [];
    incoming.on("data", (chunk: Buffer) => chunks.push(chunk));
    incoming.on("end", async () => {
      const url = new URL(incoming.url ?? "/", "http://localhost");
      const request: RecordedRequest = {
        method: incoming.method ?? "GET",
        path: url.pathname,
        query: url.searchParams,
        headers: incoming.headers,
        body: Buffer.concat(chunks),
      };
      requests.push(request);
      const reply = await handler(request);
      response.writeHead(reply.status ?? 200, {
        "content-type": reply.contentType ?? "application/json",
      });
      response.end(reply.body);
    });
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address() as AddressInfo;
  return {
    baseUrl: `http://127.0.0.1:${port}`,
    requests,
    close: () =>
      new Promise<void>((resolve, reject) => {
        server.closeAllConnections();
        server.close((error) => (error ? reject(error) : resolve()));
      }),
  };
}

/**
 * Parses a recorded multipart request with the platform FormData parser.
 * 用平台自带的 FormData 解析器解析记录下的 multipart 请求。
 */
async function readUploadedFile(
  request: RecordedRequest,
): Promise<{ name: string; bytes: number[] }> {
  const form = await new Request("http://localhost/upload", {
    method: "PUT",
    headers: { "content-type": String(request.headers["content-type"]) },
    body: new Uint8Array(request.body),
  }).formData();
  const file = form.get("content");
  assert.ok(file instanceof Blob);
  return { name: (file as File).name, bytes: [...new Uint8Array(await file.arrayBuffer())] };
}

test("file service operations send the documented requests and return the Server data", async (t) => {
  const server = await startServer(async (request) => {
    switch (`${request.method} ${request.path}`) {
      case "GET /api/files/stat":
        return ok(item(request.query.get("path") ?? ""));
      case "GET /api/files/download":
        return { contentType: "application/octet-stream", body: new Uint8Array([0, 1, 254, 255]) };
      case "PUT /api/files/write":
        return ok(item(request.query.get("path") ?? ""));
      case "POST /api/files/create-directory":
        return ok(item(request.query.get("path") ?? "", "directory"));
      case "POST /api/files/move":
        return ok(item(request.query.get("destinationPath") ?? ""));
      case "DELETE /api/files/delete":
        return ok({ success: true, message: "Directory deleted successfully" });
      default:
        return fail(404, 4040000, "Not Found");
    }
  });
  t.after(server.close);
  const service = createProjectFilesService(
    createBearerApiClient({ baseUrl: server.baseUrl, token: "agw_files" }),
  );

  const stat = await service.statFile("project-1", "docs/a.md", "extra");
  const bytes = await service.downloadFile("project-1", "assets/logo.png", {}, "extra");
  const written = await service.writeFile(
    "project-1",
    "docs/b.bin",
    new Uint8Array([7, 0, 255]),
    { overwrite: true, createParents: false, expectedSha256: "a".repeat(64) },
    "extra",
  );
  const created = await service.createDirectory("project-1", "docs/archive", true, "extra");
  const moved = await service.moveFile(
    "project-1",
    "docs/a.md",
    "docs/archive/a.md",
    false,
    "extra",
  );
  await service.deleteFile("project-1", "docs/archive", "extra", false);
  await service.deleteFile("project-1", "docs/old", "extra");

  assert.deepEqual(stat, item("docs/a.md"));
  assert.deepEqual([...bytes], [0, 1, 254, 255]);
  assert.equal(written.path, "docs/b.bin");
  assert.equal(created.type, "directory");
  assert.equal(moved.path, "docs/archive/a.md");
  const [
    statRequest,
    downloadRequest,
    writeRequest,
    createRequest,
    moveRequest,
    deleteRequest,
    defaultDelete,
  ] = server.requests;
  for (const request of server.requests) {
    assert.equal(request.query.get("projectId"), "project-1");
    assert.equal(request.query.get("directoryId"), "extra");
    assert.equal(request.headers.authorization, "Bearer agw_files");
  }
  assert.equal(statRequest?.query.get("path"), "docs/a.md");
  assert.equal(downloadRequest?.query.get("path"), "assets/logo.png");
  assert.equal(writeRequest?.query.get("overwrite"), "true");
  assert.equal(writeRequest?.query.get("createParents"), "false");
  assert.equal(writeRequest?.query.get("expectedSha256"), "a".repeat(64));
  assert.match(String(writeRequest?.headers["content-type"]), /^multipart\/form-data; boundary=/u);
  assert.deepEqual(await readUploadedFile(writeRequest!), { name: "b.bin", bytes: [7, 0, 255] });
  assert.equal(createRequest?.query.get("recursive"), "true");
  assert.equal(moveRequest?.query.get("sourcePath"), "docs/a.md");
  assert.equal(moveRequest?.query.get("destinationPath"), "docs/archive/a.md");
  assert.equal(moveRequest?.query.get("overwrite"), "false");
  assert.equal(deleteRequest?.query.get("recursive"), "false");
  assert.equal(defaultDelete?.query.has("recursive"), false);
});

test("omitted write and move options are not sent", async (t) => {
  const server = await startServer((request) =>
    ok(item(request.query.get("path") ?? request.query.get("destinationPath") ?? "")),
  );
  t.after(server.close);
  const service = createProjectFilesService(
    createBearerApiClient({ baseUrl: server.baseUrl, token: "agw_files" }),
  );

  await service.writeFile("project-1", "notes.md", "héllo");
  await service.createDirectory("project-1", "archive");
  await service.moveFile("project-1", "notes.md", "archive/notes.md");

  const [writeRequest, createRequest, moveRequest] = server.requests;
  assert.equal(writeRequest?.query.has("overwrite"), false);
  assert.equal(writeRequest?.query.has("createParents"), false);
  assert.equal(writeRequest?.query.has("expectedSha256"), false);
  assert.equal(writeRequest?.query.has("directoryId"), false);
  assert.deepEqual(await readUploadedFile(writeRequest!), {
    name: "notes.md",
    bytes: [...new TextEncoder().encode("héllo")],
  });
  assert.equal(createRequest?.query.has("recursive"), false);
  assert.equal(moveRequest?.query.has("overwrite"), false);
});

test("Server failures become FileApiError carrying the ApiResult code", async (t) => {
  const server = await startServer((request) => {
    if (request.path === "/api/files/delete") return fail(409, 4090023, "Directory is not empty.");
    if (request.path === "/api/files/download") return fail(404, 4040001, "File was not found.");
    if (request.path === "/api/files/write") {
      return fail(413, 4130001, "File exceeds the 100 MiB transfer limit.");
    }
    return fail(400, 4000040, "File already exists.");
  });
  t.after(server.close);
  const service = createProjectFilesService(
    createBearerApiClient({ baseUrl: server.baseUrl, token: "agw_files" }),
  );

  const expectations: Array<[() => Promise<unknown>, number, number, string]> = [
    [
      () => service.deleteFile("project-1", "docs", null, false),
      409,
      4090023,
      "Directory is not empty.",
    ],
    [() => service.downloadFile("project-1", "missing.md"), 404, 4040001, "File was not found."],
    [
      () => service.writeFile("project-1", "large.bin", "x"),
      413,
      4130001,
      "File exceeds the 100 MiB transfer limit.",
    ],
    [() => service.moveFile("project-1", "a.md", "b.md"), 400, 4000040, "File already exists."],
  ];
  for (const [operation, status, code, message] of expectations) {
    await assert.rejects(operation(), (caught: unknown) => {
      assert.ok(caught instanceof FileApiError);
      assert.equal(caught.status, status);
      assert.equal(caught.code, code);
      assert.equal(caught.message, message);
      return true;
    });
  }
});

test("transport failures become FileApiError without a code", async () => {
  const service = createProjectFilesService(
    createBearerApiClient({ baseUrl: "http://127.0.0.1:9", token: "agw_files" }),
  );

  await assert.rejects(service.statFile("project-1", "a.md"), (caught: unknown) => {
    assert.ok(caught instanceof FileApiError);
    assert.equal(caught.code, null);
    assert.equal(caught.status, undefined);
    return true;
  });
});

test("default browser functions write and download through the configured API runtime", async (t) => {
  const server = await startServer((request) => {
    if (request.path === "/api/auth/antiforgery") return ok({ requestToken: "csrf-files" });
    if (request.path === "/api/files/download") {
      return { contentType: "application/octet-stream", body: new Uint8Array([9, 8]) };
    }
    if (request.path === "/api/files/delete") return ok({ success: true, message: "deleted" });
    return ok(item("docs/c.txt"));
  });
  t.after(async () => {
    resetApiRuntime();
    await server.close();
  });
  configureApiRuntime({ baseUrl: server.baseUrl, token: "agw_desktop" });
  const controller = new AbortController();

  const written = await writeFile("project-1", "docs/c.txt", "text", {
    createParents: true,
    signal: controller.signal,
  });
  const bytes = await downloadFile("project-1", "docs/c.txt", { signal: controller.signal });
  await deleteFile("project-1", "docs", undefined, null, false);

  assert.equal(written.path, "docs/c.txt");
  assert.deepEqual([...bytes], [9, 8]);
  const writeRequest = server.requests.find((request) => request.path === "/api/files/write");
  assert.equal(writeRequest?.method, "PUT");
  assert.equal(writeRequest?.headers["x-csrf-token"], "csrf-files");
  assert.equal(writeRequest?.headers.authorization, "Bearer agw_desktop");
  assert.equal(writeRequest?.query.get("createParents"), "true");
  const deleteRequest = server.requests.find((request) => request.path === "/api/files/delete");
  assert.equal(deleteRequest?.query.get("recursive"), "false");
});
