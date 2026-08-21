import { createUuidV7 } from "@agw/api";
import {
  MAX_IMAGE_ATTACHMENTS,
  isSupportedImageMediaType,
  validateImageAttachments,
  type ChatImageAttachment,
} from "@agw/chat-core";
import { File } from "expo-file-system";
import * as ImagePicker from "expo-image-picker";

export async function pickChatImages(
  existingAttachments: readonly ChatImageAttachment[],
): Promise<ChatImageAttachment[]> {
  const remaining = MAX_IMAGE_ATTACHMENTS - existingAttachments.length;
  if (remaining <= 0) throw new Error(`You can attach up to ${MAX_IMAGE_ATTACHMENTS} images.`);

  const result = await ImagePicker.launchImageLibraryAsync({
    mediaTypes: ["images"],
    allowsMultipleSelection: true,
    selectionLimit: remaining,
    orderedSelection: true,
    allowsEditing: false,
    quality: 1,
    shouldDownloadFromNetwork: true,
  });
  if (result.canceled) return [];

  const descriptors = result.assets.map((asset, index) => {
    const file = new File(asset.uri);
    return {
      asset,
      file,
      name: asset.fileName || file.name || `selected-image-${index + 1}`,
      size: asset.fileSize ?? file.size,
      type: asset.mimeType || file.type,
    };
  });
  const validationError = validateImageAttachments(descriptors, existingAttachments);
  if (validationError) throw new Error(validationError);

  return Promise.all(
    descriptors.map(async ({ file, name, size, type }) => {
      if (!isSupportedImageMediaType(type)) {
        throw new Error("Unsupported image type. Use JPEG, PNG, GIF, or WebP.");
      }
      return {
        id: createUuidV7(),
        name,
        size,
        mediaType: type,
        dataUrl: `data:${type};base64,${await file.base64()}`,
      };
    }),
  );
}
