import { getTurnNotificationContent, toTurnNotifyStatus } from "@/features/chat/turn-notification";

test("toTurnNotifyStatus accepts only terminal notify statuses", () => {
  expect(toTurnNotifyStatus("completed")).toBe("completed");
  expect(toTurnNotifyStatus("failed")).toBe("failed");
  expect(toTurnNotifyStatus("interrupted")).toBeNull();
});

test("turn notification text is generic and never includes conversation content", () => {
  expect(getTurnNotificationContent("completed")).toEqual({
    title: "Turn completed",
    body: expect.stringContaining("Agw"),
  });
  expect(getTurnNotificationContent("failed")).toEqual({
    title: "Turn failed",
    body: expect.stringContaining("Agw"),
  });
});
