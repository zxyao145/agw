import { buildMobileModeCommand, buildMobileSettingCommand } from "@/features/chat/execution-ws";

describe("Mobile execution settings", () => {
  test("sends the Composer permission mode to the execution hub", () => {
    expect(
      buildMobileSettingCommand({
        projectId: "project-1",
        contextId: "context-1",
        permissionMode: "alwaysAsk",
      }),
    ).toEqual({
      type: "SettingCommand",
      projectId: "project-1",
      contextId: "context-1",
      permissionMode: "alwaysAsk",
    });
  });

  test("sets the selected Agent mode before execution", () => {
    expect(
      buildMobileModeCommand({
        agentId: "agent-1",
        agentType: 0,
        agentMode: "plan",
      }),
    ).toEqual({
      type: "SetModeCommand",
      agentId: "agent-1",
      mode: "plan",
    });
  });

  test("does not send Agent mode commands to Agentflows", () => {
    expect(
      buildMobileModeCommand({
        agentId: "agentflow-1",
        agentType: 1,
        agentMode: "plan",
      }),
    ).toBeNull();
  });
});
