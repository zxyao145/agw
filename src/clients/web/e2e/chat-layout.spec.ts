import { expect, test, type Page } from "@playwright/test";

import { conversationId, mockChatApi, projectId } from "./chat-api";

// 聊天区域、对话列与输入框区域在页面中的实际位置。The rendered boxes of the chat area, the Chat column and the composer area.
async function chatBoxes(page: Page) {
  const area = await page.locator(".agw-chat-column-area").boundingBox();
  const column = await page.locator(".agw-chat-column").boundingBox();
  const composer = await page.locator(".agw-chat-composer").boundingBox();
  if (!area || !column || !composer) throw new Error("The Chat column is not rendered.");
  return { area, column, composer };
}

test("a new chat shows only the composer in the middle of the chat area", async ({ page }) => {
  await mockChatApi(page);
  await page.goto(`/chat/?projectId=${projectId}`);
  await expect(page.locator(".agw-chat-composer textarea")).toBeVisible();

  const { area, column, composer } = await chatBoxes(page);
  expect(Math.abs(column.y - composer.y)).toBeLessThanOrEqual(1);
  expect(Math.abs(column.height - composer.height)).toBeLessThanOrEqual(1);
  const center = (box: { y: number; height: number }) => box.y + box.height / 2;
  expect(Math.abs(center(composer) - center(area))).toBeLessThanOrEqual(1);
  await expect(page.getByRole("list", { name: "Conversation messages" })).toHaveCount(0);
});

test("a conversation with messages keeps the composer at the bottom", async ({ page }) => {
  await mockChatApi(page);
  await page.goto(`/chat/?projectId=${projectId}&conversationId=${conversationId}`);
  await expect(page.getByText("Body with")).toBeVisible();

  const { column, composer } = await chatBoxes(page);
  expect(Math.abs(column.y + column.height - (composer.y + composer.height))).toBeLessThanOrEqual(
    1,
  );
});

test("an Agentflow conversation lists its nodes between Token usage and Todo", async ({
  page,
}, testInfo) => {
  const isMobile = testInfo.project.name === "mobile-chromium";
  if (!isMobile) await page.setViewportSize({ width: 1728, height: 900 });
  await mockChatApi(page, { withAgentflow: true });
  await page.goto(`/chat/?projectId=${projectId}&conversationId=${conversationId}`);
  const card = page.getByLabel("Agentflow nodes");
  await expect(card.locator("ol > li")).toHaveCount(40);
  if (isMobile) {
    await expect(card).toBeHidden();
    return;
  }

  await expect(card).toBeVisible();
  await expect(card.locator("ol > li").first()).toHaveText("Input");
  const tokenUsage = page.getByText("Token usage", { exact: true }).locator("..");
  const todo = page.getByLabel("Current turn todos");
  await expect(todo).toBeInViewport();
  const [tokenBox, cardBox, todoBox] = await Promise.all([
    tokenUsage.boundingBox(),
    card.boundingBox(),
    todo.boundingBox(),
  ]);
  if (!tokenBox || !cardBox || !todoBox)
    throw new Error("The conversation sidebar is not rendered.");
  expect(cardBox.y).toBeGreaterThanOrEqual(tokenBox.y + tokenBox.height);
  expect(todoBox.y).toBeGreaterThanOrEqual(cardBox.y + cardBox.height);

  const list = card.locator("ol").locator("..");
  const { clientHeight, scrollHeight } = await list.evaluate((element) => ({
    clientHeight: element.clientHeight,
    scrollHeight: element.scrollHeight,
  }));
  expect(clientHeight).toBeLessThanOrEqual(448);
  expect(scrollHeight).toBeGreaterThan(clientHeight);
});
