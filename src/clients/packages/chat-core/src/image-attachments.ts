export const MAX_IMAGE_ATTACHMENTS = 5;
export const MAX_IMAGE_ATTACHMENT_BYTES = 5 * 1024 * 1024;
export const MAX_IMAGE_ATTACHMENTS_TOTAL_BYTES = 10 * 1024 * 1024;

export const SUPPORTED_IMAGE_MEDIA_TYPES = [
  "image/jpeg",
  "image/png",
  "image/gif",
  "image/webp",
] as const;

export type SupportedImageMediaType = (typeof SUPPORTED_IMAGE_MEDIA_TYPES)[number];

export interface ChatImageAttachment {
  id: string;
  name: string;
  mediaType: SupportedImageMediaType;
  size: number;
  dataUrl: string;
}

export type ImageAttachmentDescriptor = {
  name: string;
  size: number;
  type: string;
};

const supportedImageMediaTypes = new Set<string>(SUPPORTED_IMAGE_MEDIA_TYPES);

export function isSupportedImageMediaType(value: string): value is SupportedImageMediaType {
  return supportedImageMediaTypes.has(value);
}

export function validateImageAttachments(
  files: readonly ImageAttachmentDescriptor[],
  existingAttachments: readonly Pick<ChatImageAttachment, "size">[],
): string | null {
  if (existingAttachments.length + files.length > MAX_IMAGE_ATTACHMENTS) {
    return `You can attach up to ${MAX_IMAGE_ATTACHMENTS} images.`;
  }

  for (const file of files) {
    if (!isSupportedImageMediaType(file.type)) {
      return "Unsupported image type. Use JPEG, PNG, GIF, or WebP.";
    }
    if (file.size > MAX_IMAGE_ATTACHMENT_BYTES) {
      return `${file.name || "Image"} exceeds the 5 MB limit.`;
    }
  }

  const existingBytes = existingAttachments.reduce(
    (total, attachment) => total + attachment.size,
    0,
  );
  const incomingBytes = files.reduce((total, file) => total + file.size, 0);
  if (existingBytes + incomingBytes > MAX_IMAGE_ATTACHMENTS_TOTAL_BYTES) {
    return "Images can total up to 10 MB.";
  }

  return null;
}
