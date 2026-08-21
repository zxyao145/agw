import { createUuidV7 } from "@agw/api";
import {
  isSupportedImageMediaType,
  validateImageAttachments,
  type ChatImageAttachment,
  type SupportedImageMediaType,
} from "./image-attachment-contracts";

export * from "./image-attachment-contracts";

const extensionByMediaType: Record<SupportedImageMediaType, string> = {
  "image/jpeg": "jpg",
  "image/png": "png",
  "image/gif": "gif",
  "image/webp": "webp",
};

export function validateImageFiles(
  files: readonly Pick<File, "name" | "size" | "type">[],
  existingAttachments: readonly Pick<ChatImageAttachment, "size">[],
): string | null {
  return validateImageAttachments(files, existingAttachments);
}

function readAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () =>
      typeof reader.result === "string"
        ? resolve(reader.result)
        : reject(new Error("The pasted image could not be read."));
    reader.onerror = () => reject(reader.error ?? new Error("The pasted image could not be read."));
    reader.readAsDataURL(file);
  });
}

export async function createImageAttachments(
  files: readonly File[],
): Promise<ChatImageAttachment[]> {
  return Promise.all(
    files.map(async (file, index) => {
      if (!isSupportedImageMediaType(file.type)) {
        throw new Error("Unsupported image type. Use JPEG, PNG, GIF, or WebP.");
      }

      return {
        id: createUuidV7(),
        name: file.name || `pasted-image-${index + 1}.${extensionByMediaType[file.type]}`,
        mediaType: file.type,
        size: file.size,
        dataUrl: await readAsDataUrl(file),
      };
    }),
  );
}
