import assert from "node:assert/strict";
import test from "node:test";

import type { ProjectResponse } from "./components/types";
import { createProjectCopyRequest } from "./copy-requests";

const sourceProject = {
  id: "project-1",
  name: "research-project",
  type: 0,
  description: "Researches a topic",
  workspace: "~/custom/research-project",
  extraSetting: '{"mode":"thorough"}',
  tools: [
    {
      kind: "tool",
      definition: {
        name: "generate_guid",
        options: {},
      },
    },
  ],
  environmentVariables: {
    SEARCH_TOKEN: "secret",
  },
  projectMcpToolServers: [
    {
      projectId: "project-1",
      mcpToolServerId: "mcp-1",
    },
  ],
  projectSkillRelations: [
    {
      projectId: "project-1",
      skillId: "skill-1",
    },
  ],
  projectConnectionRelations: [
    {
      projectId: "project-1",
      connectionId: "connection-1",
    },
  ],
  createTime: "2026-08-19T01:00:00Z",
  createBy: "admin",
  updateTime: "2026-08-19T02:00:00Z",
  updateBy: "admin",
} satisfies ProjectResponse;

test("createProjectCopyRequest copies all Project configuration with a new identity", () => {
  const request = createProjectCopyRequest(sourceProject, "12345678-abcd-efab-cdef-1234567890ab");

  assert.deepEqual(request, {
    name: "research-project-copy-12345678",
    description: "Researches a topic",
    workspace: null,
    extraSetting: '{"mode":"thorough"}',
    tools: sourceProject.tools,
    mcpToolServerIds: ["mcp-1"],
    skillIds: ["skill-1"],
    connectionIds: ["connection-1"],
    environmentVariables: {
      SEARCH_TOKEN: "secret",
    },
  });
  assert.notStrictEqual(request.tools, sourceProject.tools);
  assert.notStrictEqual(request.environmentVariables, sourceProject.environmentVariables);
  assert.equal("id" in request, false);
  assert.equal("type" in request, false);
  assert.equal("createTime" in request, false);
  assert.equal("updateTime" in request, false);
});

test("createProjectCopyRequest derives a new workspace from the copied name", () => {
  const request = createProjectCopyRequest(sourceProject, "12345678");

  assert.equal(request.workspace, null);
});

test("createProjectCopyRequest keeps generated names within the database length limit", () => {
  const request = createProjectCopyRequest(
    {
      ...sourceProject,
      name: "p".repeat(200),
    },
    "abcd-ef12-3456-7890",
  );

  assert.equal(request.name.length, 200);
  assert.equal(request.name.endsWith("-copy-abcdef12"), true);
});

test("createProjectCopyRequest represents empty relations as null", () => {
  const request = createProjectCopyRequest(
    {
      ...sourceProject,
      projectMcpToolServers: [],
      projectSkillRelations: [],
      projectConnectionRelations: [],
    },
    "12345678",
  );

  assert.equal(request.mcpToolServerIds, null);
  assert.equal(request.skillIds, null);
  assert.equal(request.connectionIds, null);
});
