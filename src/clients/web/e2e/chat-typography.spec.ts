import { expect, test } from "@playwright/test";

import { conversationId, mockChatApi, projectId } from "./chat-api";

test("chat Markdown uses the compact typography of the shared prose", async ({ page }) => {
  await mockChatApi(page);
  await page.goto(`/chat/?projectId=${projectId}&conversationId=${conversationId}`);
  const prose = page.locator('.agw-prose[data-density="compact"]', { hasText: "Body with" });
  await expect(prose.locator("h1")).toHaveText("Heading");

  const fontSize = (selector: string) =>
    prose.locator(selector).evaluate((element) => getComputedStyle(element).fontSize);
  expect(await fontSize("p")).toBe("14px");
  expect(await fontSize("h1")).toBe("24.5px");
  expect(await fontSize("p code")).toBe("12.25px");
});
