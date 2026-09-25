import type { ConversationRenderItem, PresentedContent } from "@agw/chat-core";
import { isUserTurnMessage } from "@agw/execution-core";
import type { ConversationTurnInputSummary } from "@agw/projects";
import { isNonEmptyGuid } from "@agw/api";

export const USER_INPUT_PREVIEW_MAX_LENGTH = 160;
export const USER_INPUT_NAVIGATION_ACTIVATION_OFFSET = 32;

export type UserInputAnchor = {
  key: string;
  itemIndex: number | null;
  preview: string;
};

export type UserInputMarker = UserInputAnchor & {
  rowIndex: number | null;
  start: number | null;
};

type RowMeasurement = {
  start: number;
};

function normalizePreview(value: string): string {
  return value.replace(/\s+/gu, " ").trim();
}

export function truncateUserInputPreview(
  value: string,
  maxLength = USER_INPUT_PREVIEW_MAX_LENGTH,
): string {
  const normalized = normalizePreview(value);
  const characters = [...normalized];
  if (characters.length <= maxLength) return normalized;
  if (maxLength <= 1) return "…";

  return `${characters
    .slice(0, maxLength - 1)
    .join("")
    .trimEnd()}…`;
}

function getTextPreview(contents: readonly PresentedContent[]): string {
  return normalizePreview(
    contents
      .flatMap((content) => {
        if (content.type === "markdown" || content.type === "reasoning") {
          return [content.markdown];
        }
        if (content.type === "plain" || content.type === "error") {
          return [content.text];
        }
        if (content.type === "plan") {
          return [content.leadingMarkdown, content.markdown, content.trailingMarkdown];
        }
        return [];
      })
      .join(" "),
  );
}

function getAttachmentPreview(contents: readonly PresentedContent[]): string {
  const imageNames = contents
    .filter((content) => content.type === "image")
    .map((content) => content.name)
    .filter((name): name is string => Boolean(name));
  if (imageNames.length > 0) return imageNames.join(", ");
  if (contents.some((content) => content.type === "image")) return "Image input";

  const uri = contents.find((content) => content.type === "uri");
  return uri?.name ?? uri?.uri ?? "User input";
}

export function userInputKey(messageId: string): string {
  return isNonEmptyGuid(messageId) || /^[0-9a-f]{32}$/i.test(messageId)
    ? messageId.replaceAll("-", "").toLowerCase()
    : messageId;
}

export function buildUserInputAnchors(
  items: readonly ConversationRenderItem[],
  inputs: readonly ConversationTurnInputSummary[] = [],
): UserInputAnchor[] {
  const loaded = new Map<string, UserInputAnchor>();
  items.forEach((item, itemIndex) => {
    if (
      (item.type !== "message" && item.type !== "result" && item.type !== "plan") ||
      !isUserTurnMessage(item.message.source)
    ) {
      return;
    }

    const textPreview = getTextPreview(item.message.contents);
    const preview = truncateUserInputPreview(
      textPreview || getAttachmentPreview(item.message.contents),
    );
    const key = userInputKey(item.message.source.messageId);
    loaded.set(key, { key, itemIndex, preview });
  });
  const anchors = inputs.map((input) => {
    const key = userInputKey(input.inputMessageId);
    const itemIndex = loaded.get(key)?.itemIndex ?? null;
    loaded.delete(key);
    return {
      key,
      itemIndex,
      preview: truncateUserInputPreview(input.inputSummary || "User input"),
    };
  });
  return [...anchors, ...loaded.values()];
}

export function layoutUserInputMarkers(
  anchors: readonly UserInputAnchor[],
  measurements: readonly RowMeasurement[],
  rowOffset: number,
): UserInputMarker[] {
  return anchors.map((anchor) => {
    const rowIndex = anchor.itemIndex === null ? null : anchor.itemIndex + rowOffset;
    return {
      ...anchor,
      rowIndex,
      start: rowIndex === null ? null : (measurements[rowIndex]?.start ?? null),
    };
  });
}

export function getActiveUserInputMarkerKey(
  markers: readonly UserInputMarker[],
  scrollOffset: number,
  isAtBottom = false,
  activationOffset = USER_INPUT_NAVIGATION_ACTIVATION_OFFSET,
): string | null {
  if (markers.length === 0) return null;
  if (isAtBottom) return markers[markers.length - 1].key;

  const threshold = Math.max(scrollOffset, 0) + activationOffset;
  let activeKey: string | null = null;
  for (const marker of markers) {
    if (marker.start === null) continue;
    activeKey ??= marker.key;
    if (marker.start > threshold) break;
    activeKey = marker.key;
  }
  return activeKey;
}
