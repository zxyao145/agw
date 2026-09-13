import assert from "node:assert/strict";
import test from "node:test";
import { ApiError } from "@agw/api";
import { getProjectDirectories } from "./directories";
import { createProjectFilesService } from "./files";

test("primary keeps the existing fallback and additional roots keep their identity", () => {
  const roots = getProjectDirectories({
    id: "1234-5678",
    additionalDirectories: [{ id: "extra", path: "/other/project" }],
  });
  assert.deepEqual(
    roots.map((root) => [root.id, root.path, root.isPrimary]),
    [
      [null, "~/.agw/projects/12345678", true],
      ["extra", "/other/project", false],
    ],
  );
});

test("all file and Git operations bind the selected project and directory", async () => {
  const requests: Array<{ path: string; query: Record<string, unknown> }> = [];
  const capture = async (path: string, options: { params: { query: Record<string, unknown> } }) => {
    requests.push({ path, query: options.params.query });
    return {};
  };
  const service = createProjectFilesService({
    apiGet: capture,
    apiPost: capture,
    apiDelete: capture,
  } as unknown as Parameters<typeof createProjectFilesService>[0]);
  await service.listFiles("project", "", false, true, "extra");
  await service.readFile("project", "README.md", "extra");
  await service.searchFiles("project", "", "README", true, "extra");
  await service.getFileDiff("project", "README.md", "staged", "extra");
  await service.setFileStaged("project", "README.md", true, "extra");
  await service.setFileStaged("project", "README.md", false, "extra");
  await service.resetFile("project", "README.md", "extra");
  await service.deleteFile("project", "README.md", "extra");
  assert.equal(requests.length, 8);
  for (const request of requests) {
    assert.equal(request.query.projectId, "project", request.path);
    assert.equal(request.query.directoryId, "extra", request.path);
  }
  await service.readFile("project", "README.md");
  assert.equal(requests.at(-1)?.query.directoryId, undefined);
});

test("unavailable directories display the API detail", async () => {
  const fail = async () => {
    throw new ApiError({
      status: 404,
      statusText: "Not Found",
      url: "/api/files/list",
      body: { code: 1, detail: "Project directory is unavailable: '/data/extra'." },
    });
  };
  const service = createProjectFilesService({ apiGet: fail, apiPost: fail, apiDelete: fail });
  await assert.rejects(
    service.listFiles("project", "", false, false, "extra"),
    /unavailable: '\/data\/extra'/,
  );
});
