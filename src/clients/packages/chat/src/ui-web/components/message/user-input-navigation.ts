import type { ConversationRenderItem, PresentedContent } from "@agw/chat-core";

export const USER_INPUT_PREVIEW_MAX_LENGTH = 160;
export const USER_INPUT_NAVIGATION_ACTIVATION_OFFSET = 32;

export type UserInputAnchor = {
  key: string;
  itemIndex: number;
  preview: string;
};

export type UserInputMarker = UserInputAnchor & {
  rowIndex: number;
  start: number;
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

export function buildUserInputAnchors(items: readonly ConversationRenderItem[]): UserInputAnchor[] {
  return items.flatMap((item, itemIndex) => {
    if (
      (item.type !== "message" && item.type !== "result" && item.type !== "plan") ||
      item.message.source.role !== "user"
    ) {
      return [];
    }

    const textPreview = getTextPreview(item.message.contents);
    const preview = truncateUserInputPreview(
      textPreview || getAttachmentPreview(item.message.contents),
    );
    return [{ key: item.key, itemIndex, preview }];
  });
}

export function layoutUserInputMarkers(
  anchors: readonly UserInputAnchor[],
  measurements: readonly RowMeasurement[],
  rowOffset: number,
): UserInputMarker[] {
  return anchors.flatMap((anchor) => {
    const rowIndex = anchor.itemIndex + rowOffset;
    const measurement = measurements[rowIndex];
    if (!measurement) return [];

    return [
      {
        ...anchor,
        rowIndex,
        start: measurement.start,
      },
    ];
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
  let activeKey = markers[0].key;
  for (const marker of markers) {
    if (marker.start > threshold) break;
    activeKey = marker.key;
  }
  return activeKey;
}
