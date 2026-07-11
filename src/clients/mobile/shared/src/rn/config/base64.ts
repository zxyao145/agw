const alphabet =
  "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

const decodeTable = new Map<string, number>(
  alphabet.split("").map((character, index) => [character, index])
);

export function encodeUtf8Base64Url(value: string): string {
  const bytes = encodeUtf8(value);
  let output = "";

  for (let index = 0; index < bytes.length; index += 3) {
    const first = bytes[index];
    const second = bytes[index + 1];
    const third = bytes[index + 2];
    const chunk = (first << 16) | ((second ?? 0) << 8) | (third ?? 0);

    output += alphabet[(chunk >> 18) & 63];
    output += alphabet[(chunk >> 12) & 63];
    output += second === undefined ? "=" : alphabet[(chunk >> 6) & 63];
    output += third === undefined ? "=" : alphabet[chunk & 63];
  }

  return output.replace(/=+$/g, "");
}

export function decodeUtf8Base64Url(encodedValue: string): string {
  const normalized = encodedValue.replace(/\s/g, "");

  if (!normalized) {
    throw new Error("Base64URL payload is empty.");
  }

  if (/[+/=]/.test(normalized)) {
    throw new Error(
      "Base64URL payload must not contain standard Base64 characters."
    );
  }

  if (/[^A-Za-z0-9\-_]/.test(normalized)) {
    throw new Error("Base64URL payload contains invalid characters.");
  }

  if (normalized.length % 4 === 1) {
    throw new Error("Base64URL payload length is invalid.");
  }

  const padded = normalized.padEnd(
    normalized.length + ((4 - (normalized.length % 4)) % 4),
    "="
  );
  const bytes: number[] = [];

  for (let index = 0; index < padded.length; index += 4) {
    const chars = padded.slice(index, index + 4);
    const paddingStart = chars.indexOf("=");

    if (paddingStart !== -1 && paddingStart < chars.length - 2) {
      throw new Error("Base64URL padding is invalid.");
    }

    const values = chars.split("").map((character) => {
      if (character === "=") {
        return 0;
      }

      const value = decodeTable.get(character);

      if (value === undefined) {
        throw new Error("Base64URL payload contains invalid characters.");
      }

      return value;
    });
    const chunk =
      (values[0] << 18) |
      (values[1] << 12) |
      (values[2] << 6) |
      values[3];

    bytes.push((chunk >> 16) & 255);

    if (chars[2] !== "=") {
      bytes.push((chunk >> 8) & 255);
    }

    if (chars[3] !== "=") {
      bytes.push(chunk & 255);
    }
  }

  return decodeUtf8(bytes);
}

function encodeUtf8(value: string): number[] {
  const bytes: number[] = [];

  for (let index = 0; index < value.length; index += 1) {
    let codePoint = value.codePointAt(index) ?? 0xfffd;

    if (codePoint > 0xffff) {
      index += 1;
    }

    if (codePoint >= 0xd800 && codePoint <= 0xdfff) {
      codePoint = 0xfffd;
    }

    if (codePoint <= 0x7f) {
      bytes.push(codePoint);
    } else if (codePoint <= 0x7ff) {
      bytes.push(0xc0 | (codePoint >> 6), 0x80 | (codePoint & 0x3f));
    } else if (codePoint <= 0xffff) {
      bytes.push(
        0xe0 | (codePoint >> 12),
        0x80 | ((codePoint >> 6) & 0x3f),
        0x80 | (codePoint & 0x3f)
      );
    } else {
      bytes.push(
        0xf0 | (codePoint >> 18),
        0x80 | ((codePoint >> 12) & 0x3f),
        0x80 | ((codePoint >> 6) & 0x3f),
        0x80 | (codePoint & 0x3f)
      );
    }
  }

  return bytes;
}

function decodeUtf8(bytes: number[]): string {
  const codePoints: number[] = [];

  for (let index = 0; index < bytes.length; index += 1) {
    const first = bytes[index];

    if (first <= 0x7f) {
      codePoints.push(first);
      continue;
    }

    if (first >= 0xc2 && first <= 0xdf) {
      const second = readContinuation(bytes, index + 1);
      codePoints.push(((first & 0x1f) << 6) | second);
      index += 1;
      continue;
    }

    if (first >= 0xe0 && first <= 0xef) {
      const secondRaw = bytes[index + 1];
      const thirdRaw = bytes[index + 2];
      const second = readContinuation(bytes, index + 1);
      const third = readContinuation(bytes, index + 2);

      if (
        (first === 0xe0 && secondRaw < 0xa0) ||
        (first === 0xed && secondRaw >= 0xa0)
      ) {
        throw new Error("Base64URL payload is not valid UTF-8.");
      }

      codePoints.push(((first & 0x0f) << 12) | (second << 6) | third);
      index += 2;
      continue;
    }

    if (first >= 0xf0 && first <= 0xf4) {
      const secondRaw = bytes[index + 1];
      const second = readContinuation(bytes, index + 1);
      const third = readContinuation(bytes, index + 2);
      const fourth = readContinuation(bytes, index + 3);

      if (
        (first === 0xf0 && secondRaw < 0x90) ||
        (first === 0xf4 && secondRaw > 0x8f)
      ) {
        throw new Error("Base64URL payload is not valid UTF-8.");
      }

      codePoints.push(
        ((first & 0x07) << 18) | (second << 12) | (third << 6) | fourth
      );
      index += 3;
      continue;
    }

    throw new Error("Base64URL payload is not valid UTF-8.");
  }

  return String.fromCodePoint(...codePoints);
}

function readContinuation(bytes: number[], index: number): number {
  const byte = bytes[index];

  if (byte === undefined || byte < 0x80 || byte > 0xbf) {
    throw new Error("Base64URL payload is not valid UTF-8.");
  }

  return byte & 0x3f;
}
