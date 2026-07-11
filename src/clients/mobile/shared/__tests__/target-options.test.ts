import { buildAgwTargetOptions } from "../src/rn/pages/home/lib/target-options";

const agents = [
  {
    id: "agent-codex",
    displayName: "Codex Display",
    name: "Codex",
  },
  {
    id: "agent-claude",
    displayName: "Claude Display",
    name: "ClaudeCode",
  },
  {
    id: "agent-general",
    displayName: "General Agent",
    name: "General",
  },
];

const agentflows = [
  {
    id: "flow-enabled",
    name: "Enabled Flow",
    enable: true,
  },
  {
    id: "flow-disabled",
    name: "Disabled Flow",
    enable: false,
  },
];

describe("buildAgwTargetOptions", () => {
  it("limits the Codex project to the Codex agent and hides agentflows", () => {
    expect(
      buildAgwTargetOptions({
        projectId: "11111111-1111-1111-1111-000000000004",
        agents,
        agentflows,
      })
    ).toEqual([
      {
        agentType: 0,
        id: "agent-codex",
        label: "Codex Display",
        type: "agent",
      },
    ]);
  });

  it("limits the Claude Code project to the ClaudeCode agent and hides agentflows", () => {
    expect(
      buildAgwTargetOptions({
        projectId: "11111111-1111-1111-1111-000000000002",
        agents,
        agentflows,
      })
    ).toEqual([
      {
        agentType: 0,
        id: "agent-claude",
        label: "Claude Display",
        type: "agent",
      },
    ]);
  });

  it("returns all agents and enabled agentflows for unrestricted projects", () => {
    expect(
      buildAgwTargetOptions({
        projectId: "project-general",
        agents,
        agentflows,
      })
    ).toEqual([
      {
        agentType: 0,
        id: "agent-claude",
        label: "Claude Display",
        type: "agent",
      },
      {
        agentType: 0,
        id: "agent-codex",
        label: "Codex Display",
        type: "agent",
      },
      {
        agentType: 1,
        id: "flow-enabled",
        label: "Enabled Flow",
        type: "agentflow",
      },
      {
        agentType: 0,
        id: "agent-general",
        label: "General Agent",
        type: "agent",
      },
    ]);
  });
});
