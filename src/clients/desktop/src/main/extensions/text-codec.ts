import { createHash } from "node:crypto";

import { analyse } from "chardet";
import iconv from "iconv-lite";

import type {
  DecodedText,
  EncodedText,
  EncodeTextOptions,
  TextEncodingInfo,
  TextEol,
} from "../../shared/contracts";
import { ExtensionOperationError } from "./extension-ipc-result";

type TextEncoding = TextEncodingInfo & { codec: iconv.Encoding };

/** 受支持的编码，`listTextEncodings` 按这个顺序返回。The supported encodings, returned by `listTextEncodings` in this order. */
const TEXT_ENCODINGS: readonly TextEncoding[] = [
  { id: "utf-8", label: "Unicode (UTF-8)", codec: "utf8" },
  { id: "utf-16le", label: "Unicode (UTF-16 LE)", codec: "utf16le" },
  { id: "utf-16be", label: "Unicode (UTF-16 BE)", codec: "utf16be" },
  { id: "gb18030", label: "Chinese Simplified (GB18030)", codec: "gb18030" },
  { id: "gbk", label: "Chinese Simplified (GBK)", codec: "gbk" },
  { id: "big5", label: "Chinese Traditional (Big5)", codec: "big5" },
  { id: "big5-hkscs", label: "Chinese Traditional (Big5-HKSCS)", codec: "big5hkscs" },
  { id: "shift_jis", label: "Japanese (Shift JIS)", codec: "shiftjis" },
  { id: "euc-jp", label: "Japanese (EUC-JP)", codec: "eucjp" },
  { id: "euc-kr", label: "Korean (EUC-KR)", codec: "euckr" },
  { id: "windows-1250", label: "Central European (Windows 1250)", codec: "windows1250" },
  { id: "windows-1251", label: "Cyrillic (Windows 1251)", codec: "windows1251" },
  { id: "windows-1252", label: "Western European (Windows 1252)", codec: "windows1252" },
  { id: "windows-1253", label: "Greek (Windows 1253)", codec: "windows1253" },
  { id: "windows-1254", label: "Turkish (Windows 1254)", codec: "windows1254" },
  { id: "windows-1255", label: "Hebrew (Windows 1255)", codec: "windows1255" },
  { id: "windows-1256", label: "Arabic (Windows 1256)", codec: "windows1256" },
  { id: "windows-1257", label: "Baltic (Windows 1257)", codec: "windows1257" },
  { id: "windows-1258", label: "Vietnamese (Windows 1258)", codec: "windows1258" },
  { id: "iso-8859-1", label: "Western European (ISO 8859-1)", codec: "iso88591" },
  { id: "iso-8859-2", label: "Central European (ISO 8859-2)", codec: "iso88592" },
  { id: "iso-8859-3", label: "South European (ISO 8859-3)", codec: "iso88593" },
  { id: "iso-8859-4", label: "Baltic (ISO 8859-4)", codec: "iso88594" },
  { id: "iso-8859-5", label: "Cyrillic (ISO 8859-5)", codec: "iso88595" },
  { id: "iso-8859-6", label: "Arabic (ISO 8859-6)", codec: "iso88596" },
  { id: "iso-8859-7", label: "Greek (ISO 8859-7)", codec: "iso88597" },
  { id: "iso-8859-8", label: "Hebrew (ISO 8859-8)", codec: "iso88598" },
  { id: "iso-8859-9", label: "Turkish (ISO 8859-9)", codec: "iso88599" },
  { id: "iso-8859-10", label: "Nordic (ISO 8859-10)", codec: "iso885910" },
  { id: "iso-8859-11", label: "Thai (ISO 8859-11)", codec: "iso885911" },
  { id: "iso-8859-13", label: "Baltic (ISO 8859-13)", codec: "iso885913" },
  { id: "iso-8859-14", label: "Celtic (ISO 8859-14)", codec: "iso885914" },
  { id: "iso-8859-15", label: "Western European (ISO 8859-15)", codec: "iso885915" },
  { id: "iso-8859-16", label: "South-Eastern European (ISO 8859-16)", codec: "iso885916" },
  { id: "koi8-r", label: "Cyrillic (KOI8-R)", codec: "koi8r" },
  { id: "koi8-u", label: "Cyrillic (KOI8-U)", codec: "koi8u" },
  { id: "macintosh", label: "Western European (Mac Roman)", codec: "macintosh" },
  { id: "ibm866", label: "Cyrillic (IBM 866)", codec: "ibm866" },
];

const ENCODINGS_BY_ID = new Map(TEXT_ENCODINGS.map((encoding) => [encoding.id, encoding]));

/** 可以带 BOM 的编码，按识别顺序排列。The encodings that can have a BOM, in detection order. */
const BOMS: ReadonlyMap<string, Uint8Array> = new Map([
  ["utf-8", Uint8Array.of(0xef, 0xbb, 0xbf)],
  ["utf-16le", Uint8Array.of(0xff, 0xfe)],
  ["utf-16be", Uint8Array.of(0xfe, 0xff)],
]);

const NO_BOM = new Uint8Array(0);

/** 交给 chardet 统计的字节数。The number of bytes chardet analyses. */
const DETECTION_SAMPLE_BYTES = 64 * 1024;

const LINE_BREAK = /\r\n|\r|\n/gu;

function invalidArgument(message: string): ExtensionOperationError {
  return new ExtensionOperationError("ERR_INVALID_ARGUMENT", message);
}

function supportedEncoding(id: string): TextEncoding {
  const encoding = ENCODINGS_BY_ID.get(id);
  if (!encoding) {
    throw new ExtensionOperationError(
      "ERR_UNSUPPORTED_ENCODING",
      `Unsupported text encoding: ${String(id)}.`,
    );
  }
  return encoding;
}

function contentHash(bytes: Uint8Array): string {
  return createHash("sha256").update(bytes).digest("hex");
}

function startsWith(bytes: Uint8Array, prefix: Uint8Array): boolean {
  return bytes.length >= prefix.length && prefix.every((value, index) => bytes[index] === value);
}

/**
 * U+FFFD 与控制字符（制表符、换行、垂直制表、换页、回车、ESC 除外）说明解码结果不是文字。
 * U+FFFD and control characters other than tab, line feed, vertical tab, form feed, carriage return and ESC mean the result is not text.
 */
function isRejectedCharacter(code: number): boolean {
  if (code === 0xfffd) return true;
  if (code <= 0x1f) return (code < 0x09 || code > 0x0d) && code !== 0x1b;
  return code >= 0x7f && code <= 0x9f;
}

function hasRejectedCharacter(text: string): boolean {
  for (let index = 0; index < text.length; index += 1) {
    if (isRejectedCharacter(text.charCodeAt(index))) return true;
  }
  return false;
}

/**
 * 前 64 KiB 解码后是否已有被拒绝的字符。流式解码器的 `write` 只输出完整的字符，末尾不完整的字节留在解码器中，
 * 输出是完整解码结果的前缀，这里有被拒绝的字符时完整的结果中同样有。
 * Whether the first 64 KiB already decode to a rejected character. The streaming decoder's `write` emits only complete
 * characters and keeps incomplete trailing bytes, so its output is a prefix of the full result and shares its rejected characters.
 */
function sampleHasRejectedCharacter(bytes: Uint8Array, encoding: TextEncoding): boolean {
  const sample = Buffer.from(bytes.buffer, bytes.byteOffset, DETECTION_SAMPLE_BYTES);
  return hasRejectedCharacter(iconv.getDecoder(encoding.codec, { stripBOM: false }).write(sample));
}

/**
 * 第 6 步的检查：解码后再编码得到相同的字节，并且结果中没有被拒绝的字符。不通过时返回 null。
 * 超过 64 KiB 的输入先检查样本，二进制文件不必完整解码。
 * The step 6 check: decoding then encoding gives the same bytes and the result has no rejected character. Returns null when it fails.
 * Inputs over 64 KiB check the sample first, so binary files are not decoded in full.
 */
function decodeLosslessly(bytes: Uint8Array, encoding: TextEncoding): string | null {
  if (bytes.length > DETECTION_SAMPLE_BYTES && sampleHasRejectedCharacter(bytes, encoding)) {
    return null;
  }
  const text = iconv.decode(bytes, encoding.codec, { stripBOM: false });
  if (hasRejectedCharacter(text)) return null;
  return iconv.encode(text, encoding.codec).equals(bytes) ? text : null;
}

/** 严格的 UTF-8 解码，字节不是合法的 UTF-8 时返回 null。Strict UTF-8 decoding; null when the bytes are not valid UTF-8. */
function decodeUtf8(bytes: Uint8Array): string | null {
  try {
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch (error) {
    if (error instanceof TypeError) return null;
    throw error;
  }
}

function detectEol(text: string): TextEol {
  let crlf = 0;
  let lf = 0;
  for (let index = text.indexOf("\n"); index !== -1; index = text.indexOf("\n", index + 1)) {
    if (index > 0 && text.charCodeAt(index - 1) === 0x0d) crlf += 1;
    else lf += 1;
  }
  return crlf > lf ? "\r\n" : "\n";
}

function decoded(text: string, encoding: string, bom: boolean, sha256: string): DecodedText {
  return { text, encoding, bom, eol: detectEol(text), sha256 };
}

/** chardet 给出的、在列表中的候选编码，置信度从高到低。chardet candidates in the list, by descending confidence. */
function detectedCandidates(bytes: Uint8Array): TextEncoding[] {
  return analyse(bytes.subarray(0, DETECTION_SAMPLE_BYTES))
    .sort((left, right) => right.confidence - left.confidence)
    .flatMap((match) => ENCODINGS_BY_ID.get(match.name.toLowerCase()) ?? []);
}

/**
 * 没有 BOM 的 UTF-16 候选（chardet 只按 BOM 识别 UTF-16）：字节含有 NUL 并且字节数为偶数时，
 * NUL 在偶数位置更多时先试 utf-16be，否则（包括数量相同）先试 Windows 上常见的 utf-16le。
 * BOM-less UTF-16 candidates (chardet recognizes UTF-16 only by its BOM): when the bytes contain NUL and their count is even,
 * utf-16be comes first if more NUL bytes sit at even offsets, otherwise (ties included) utf-16le, the common order on Windows.
 */
function utf16Candidates(bytes: Uint8Array): TextEncoding[] {
  if (bytes.length % 2 !== 0 || !bytes.includes(0)) return [];
  let odd = 0;
  let even = 0;
  for (let index = bytes.indexOf(0); index !== -1; index = bytes.indexOf(0, index + 1)) {
    if (index % 2 === 1) odd += 1;
    else even += 1;
  }
  const littleEndian = ENCODINGS_BY_ID.get("utf-16le")!;
  const bigEndian = ENCODINGS_BY_ID.get("utf-16be")!;
  return even > odd ? [bigEndian, littleEndian] : [littleEndian, bigEndian];
}

/** 第 7 步：没有编码通过检查。Step 7: no encoding passed the check. */
function undecodable(bytes: Uint8Array): ExtensionOperationError {
  if (bytes.includes(0)) {
    return new ExtensionOperationError("ERR_NOT_TEXT", "The file is not a text file.");
  }
  return new ExtensionOperationError(
    "ERR_UNKNOWN_ENCODING",
    "The text encoding of the file cannot be detected. Choose an encoding to read it.",
  );
}

/** 受支持的编码列表。The supported encodings. */
export function listTextEncodings(): TextEncodingInfo[] {
  return TEXT_ENCODINGS.map(({ id, label }) => ({ id, label }));
}

/**
 * 识别编码并解码（3.3 节）。有 BOM 时只尝试 BOM 对应的编码，它同样要通过第 6 步的检查；
 * 没有 BOM 时，chardet 的候选都不通过后再按 NUL 的位置尝试 UTF-16。
 * Detects the encoding and decodes (section 3.3). With a BOM only its encoding is tried, and it must pass the step 6 check too;
 * without one, UTF-16 is tried by NUL position after every chardet candidate fails.
 */
export function decodeText(bytes: Uint8Array, encoding?: string): DecodedText {
  if (!(bytes instanceof Uint8Array)) throw invalidArgument("bytes must be a Uint8Array.");
  const sha256 = contentHash(bytes);

  if (encoding !== undefined) {
    const explicit = supportedEncoding(encoding);
    const bom = BOMS.get(explicit.id);
    const bomLength = bom && startsWith(bytes, bom) ? bom.length : 0;
    const text = decodeLosslessly(bytes.subarray(bomLength), explicit);
    if (text === null) {
      throw new ExtensionOperationError(
        "ERR_UNKNOWN_ENCODING",
        `The file cannot be read losslessly as ${explicit.id}.`,
      );
    }
    return decoded(text, explicit.id, bomLength > 0, sha256);
  }

  const bomEntry = [...BOMS].find(([, bom]) => startsWith(bytes, bom));
  if (bomEntry) {
    const [id, bom] = bomEntry;
    const text = decodeLosslessly(bytes.subarray(bom.length), ENCODINGS_BY_ID.get(id)!);
    if (text === null) throw undecodable(bytes);
    return decoded(text, id, true, sha256);
  }

  if (!bytes.includes(0)) {
    const text = decodeUtf8(bytes);
    if (text !== null) return decoded(text, "utf-8", false, sha256);
  }

  for (const candidate of [...detectedCandidates(bytes), ...utf16Candidates(bytes)]) {
    const text = decodeLosslessly(bytes, candidate);
    if (text !== null) return decoded(text, candidate.id, false, sha256);
  }
  throw undecodable(bytes);
}

function isHighSurrogate(code: number): boolean {
  return code >= 0xd800 && code <= 0xdbff;
}

function isLowSurrogate(code: number): boolean {
  return code >= 0xdc00 && code <= 0xdfff;
}

/** 第一个编码后再解码发生变化的字符，行号与列号从 1 开始。The first character changed by the round trip, with 1-based line and column. */
function unencodable(text: string, roundTrip: string, encoding: string): ExtensionOperationError {
  let index = 0;
  while (index < text.length && text.charCodeAt(index) === roundTrip.charCodeAt(index)) index += 1;
  // 差异出现在代理对的低位时回到高位。Step back to the high surrogate when the difference is on a low surrogate.
  if (
    index > 0 &&
    isLowSurrogate(text.charCodeAt(index)) &&
    isHighSurrogate(text.charCodeAt(index - 1))
  ) {
    index -= 1;
  }
  let line = 1;
  let lineStart = 0;
  for (
    let found = text.indexOf("\n");
    found !== -1 && found < index;
    found = text.indexOf("\n", found + 1)
  ) {
    line += 1;
    lineStart = found + 1;
  }
  // 列号按字符（code point）计算。The column counts characters (code points).
  const column = Array.from(text.slice(lineStart, index)).length + 1;
  const codePoint = text.codePointAt(index)!;
  const unicode = codePoint.toString(16).toUpperCase().padStart(4, "0");
  return new ExtensionOperationError(
    "ERR_UNENCODABLE",
    `The character "${String.fromCodePoint(codePoint)}" (U+${unicode}) at line ${line}, column ${column} cannot be encoded as ${encoding}.`,
  );
}

/**
 * 把全部换行替换为 `eol` 后按编码写出（3.4 节第 1、2 步），返回字节与内容哈希。
 * Replaces every line break with `eol` and encodes (section 3.4 steps 1 and 2), returning the bytes and their hash.
 */
export function encodeText(text: string, options: EncodeTextOptions): EncodedText {
  if (typeof text !== "string") throw invalidArgument("text must be a string.");
  if (options.eol !== "\n" && options.eol !== "\r\n") {
    throw invalidArgument('eol must be "\\n" or "\\r\\n".');
  }
  if (typeof options.bom !== "boolean") throw invalidArgument("bom must be a boolean.");
  const encoding = supportedEncoding(options.encoding);
  const bom = options.bom ? BOMS.get(encoding.id) : NO_BOM;
  if (!bom) {
    throw invalidArgument(
      `Only utf-8, utf-16le and utf-16be can have a BOM; ${encoding.id} cannot.`,
    );
  }

  const normalized = text.replace(LINE_BREAK, options.eol);
  const encoded = iconv.encode(normalized, encoding.codec);
  const roundTrip = iconv.decode(encoded, encoding.codec, { stripBOM: false });
  if (roundTrip !== normalized) throw unencodable(normalized, roundTrip, encoding.id);

  const bytes = new Uint8Array(bom.length + encoded.length);
  bytes.set(bom);
  bytes.set(encoded, bom.length);
  return { bytes, sha256: contentHash(bytes) };
}
