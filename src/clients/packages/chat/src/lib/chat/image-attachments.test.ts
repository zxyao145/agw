import assert from "node:assert/strict";
import test from "node:test";

import {
  MAX_IMAGE_ATTACHMENT_BYTES,
  MAX_IMAGE_ATTACHMENTS_TOTAL_BYTES,
  validateImageFiles,
} from "./image-attachments";

const image = (name: string, size: number, type = "image/png") => ({ name, size, type });

test("image validation accepts the supported formats", () => {
  const error = validateImageFiles(
    [
      image("one.jpg", 1, "image/jpeg"),
      image("two.png", 1),
      image("three.gif", 1, "image/gif"),
      image("four.webp", 1, "image/webp"),
    ],
    [],
  );

  assert.equal(error, null);
});

test("image validation includes existing attachments in the count", () => {
  const error = validateImageFiles(
    [image("new.png", 1)],
    Array.from({ length: 5 }, () => ({ size: 1 })),
  );

  assert.equal(error, "You can attach up to 5 images.");
});

test("image validation rejects unsupported formats", () => {
  assert.equal(
    validateImageFiles([image("image.svg", 1, "image/svg+xml")], []),
    "Unsupported image type. Use JPEG, PNG, GIF, or WebP.",
  );
});

test("image validation enforces the per-image byte limit", () => {
  assert.equal(
    validateImageFiles([image("large.png", MAX_IMAGE_ATTACHMENT_BYTES + 1)], []),
    "large.png exceeds the 5 MB limit.",
  );
});

test("image validation includes existing attachments in the total byte limit", () => {
  assert.equal(
    validateImageFiles([image("new.png", 2)], [{ size: MAX_IMAGE_ATTACHMENTS_TOTAL_BYTES - 1 }]),
    "Images can total up to 10 MB.",
  );
});
