const LOCAL_FILE_HEADER_SIGNATURE = 0x04034b50;
const CENTRAL_DIRECTORY_HEADER_SIGNATURE = 0x02014b50;
const END_OF_CENTRAL_DIRECTORY_SIGNATURE = 0x06054b50;
const LOCAL_FILE_HEADER_SIZE = 30;
const CENTRAL_DIRECTORY_HEADER_SIZE = 46;
const END_OF_CENTRAL_DIRECTORY_SIZE = 22;
const MAX_ZIP_COMMENT_SIZE = 0xffff;
const ENCRYPTED_FLAG = 0x1;
const STORED_METHOD = 0;
const DEFLATED_METHOD = 8;
const MAX_SKILL_MARKDOWN_BYTES = 1024 * 1024;

const SKILL_MARKDOWN_PATH_REGEX = /(?:^|\/)SKILL\.md$/;
const MACOS_METADATA_PATH_REGEX = /^__MACOSX\//i;
const FRONTMATTER_REGEX = /^\uFEFF?---[ \t]*\n([\s\S]*?)\n---[ \t]*(?:\n|$)/;
const DESCRIPTION_KEY_REGEX = /^description[ \t]*:(?=\s|$)/i;
const INDENTED_OR_BLANK_LINE_REGEX = /^(?:[ \t]|$)/;
const DOUBLE_QUOTED_REGEX = /^"((?:[^"\\]|\\[\s\S])*)"/;
const SINGLE_QUOTED_REGEX = /^'((?:[^']|'')*)'/;
const DOUBLE_QUOTED_ESCAPES: Record<string, string> = { n: "\n", r: "\r", t: "\t", "0": "\0" };

type ZipEntry = {
  path: string;
  flags: number;
  compressionMethod: number;
  compressedSize: number;
  uncompressedSize: number;
  localHeaderOffset: number;
};

export async function readSkillArchiveDescription(archive: Blob): Promise<string | null> {
  try {
    const entry = findSkillMarkdownEntry(await readZipEntries(archive));
    const markdown = entry ? await readZipEntryText(archive, entry) : null;
    return markdown === null ? null : parseSkillMarkdownDescription(markdown);
  } catch {
    return null;
  }
}

export function parseSkillMarkdownDescription(markdown: string): string | null {
  const frontmatter = FRONTMATTER_REGEX.exec(markdown.replace(/\r\n?/g, "\n"))?.[1];
  const lines = frontmatter?.split("\n") ?? [];
  const keyIndex = lines.findIndex((line) => DESCRIPTION_KEY_REGEX.test(line));
  if (keyIndex < 0) return null;

  const nextKeyIndex = lines.findIndex(
    (line, index) => index > keyIndex && !INDENTED_OR_BLANK_LINE_REGEX.test(line),
  );
  const continuation = lines.slice(keyIndex + 1, nextKeyIndex < 0 ? undefined : nextKeyIndex);
  const value = lines[keyIndex].replace(DESCRIPTION_KEY_REGEX, "").trim();
  return parseYamlScalar(value, continuation).trim() || null;
}

function parseYamlScalar(value: string, continuation: string[]): string {
  if (value.startsWith("|") || value.startsWith(">")) {
    const indent = Math.min(
      ...continuation
        .filter((line) => line.trim())
        .map((line) => line.length - line.trimStart().length),
    );
    const lines = continuation.map((line) => line.slice(indent));
    return value.startsWith("|") ? lines.join("\n") : foldYamlLines(lines);
  }

  const text = foldYamlLines([value, ...continuation.map((line) => line.trim())]);
  const doubleQuoted = DOUBLE_QUOTED_REGEX.exec(text)?.[1];
  if (doubleQuoted !== undefined) {
    return doubleQuoted.replace(/\\(u[0-9a-fA-F]{4}|[\s\S])/g, (_, escape: string) =>
      escape.length > 1
        ? String.fromCharCode(Number.parseInt(escape.slice(1), 16))
        : (DOUBLE_QUOTED_ESCAPES[escape] ?? escape),
    );
  }

  const singleQuoted = SINGLE_QUOTED_REGEX.exec(text)?.[1];
  return singleQuoted === undefined ? text : singleQuoted.replaceAll("''", "'");
}

function foldYamlLines(lines: string[]): string {
  return lines.reduce((folded, line) => {
    if (!line) return `${folded}\n`;
    return !folded || folded.endsWith("\n") ? `${folded}${line}` : `${folded} ${line}`;
  }, "");
}

async function readView(blob: Blob, start: number, end: number): Promise<DataView> {
  return new DataView(await blob.slice(start, end).arrayBuffer());
}

async function readZipEntries(archive: Blob): Promise<ZipEntry[]> {
  const tail = await readView(
    archive,
    Math.max(0, archive.size - END_OF_CENTRAL_DIRECTORY_SIZE - MAX_ZIP_COMMENT_SIZE),
    archive.size,
  );
  let end = tail.byteLength - END_OF_CENTRAL_DIRECTORY_SIZE;
  while (end >= 0 && tail.getUint32(end, true) !== END_OF_CENTRAL_DIRECTORY_SIGNATURE) end--;
  if (end < 0) return [];

  const entryCount = tail.getUint16(end + 10, true);
  const directoryOffset = tail.getUint32(end + 16, true);
  const directory = await readView(
    archive,
    directoryOffset,
    directoryOffset + tail.getUint32(end + 12, true),
  );
  const decoder = new TextDecoder();
  const entries: ZipEntry[] = [];
  let position = 0;
  for (let index = 0; index < entryCount; index++) {
    if (directory.getUint32(position, true) !== CENTRAL_DIRECTORY_HEADER_SIGNATURE) return [];

    const nameStart = position + CENTRAL_DIRECTORY_HEADER_SIZE;
    const nameLength = directory.getUint16(position + 28, true);
    entries.push({
      path: decoder
        .decode(new Uint8Array(directory.buffer, nameStart, nameLength))
        .replaceAll("\\", "/"),
      flags: directory.getUint16(position + 8, true),
      compressionMethod: directory.getUint16(position + 10, true),
      compressedSize: directory.getUint32(position + 20, true),
      uncompressedSize: directory.getUint32(position + 24, true),
      localHeaderOffset: directory.getUint32(position + 42, true),
    });
    position =
      nameStart +
      nameLength +
      directory.getUint16(position + 30, true) +
      directory.getUint16(position + 32, true);
  }

  return entries;
}

function findSkillMarkdownEntry(entries: ZipEntry[]): ZipEntry | undefined {
  const depth = (entry: ZipEntry) => entry.path.split("/").length;
  return entries
    .filter(
      (entry) =>
        SKILL_MARKDOWN_PATH_REGEX.test(entry.path) && !MACOS_METADATA_PATH_REGEX.test(entry.path),
    )
    .sort((left, right) => depth(left) - depth(right))[0];
}

async function readZipEntryText(archive: Blob, entry: ZipEntry): Promise<string | null> {
  if (
    (entry.flags & ENCRYPTED_FLAG) !== 0 ||
    entry.uncompressedSize > MAX_SKILL_MARKDOWN_BYTES ||
    (entry.compressionMethod !== STORED_METHOD && entry.compressionMethod !== DEFLATED_METHOD)
  ) {
    return null;
  }

  const header = await readView(
    archive,
    entry.localHeaderOffset,
    entry.localHeaderOffset + LOCAL_FILE_HEADER_SIZE,
  );
  if (header.getUint32(0, true) !== LOCAL_FILE_HEADER_SIGNATURE) return null;

  const dataStart =
    entry.localHeaderOffset +
    LOCAL_FILE_HEADER_SIZE +
    header.getUint16(26, true) +
    header.getUint16(28, true);
  const data = archive.slice(dataStart, dataStart + entry.compressedSize);
  const bytes =
    entry.compressionMethod === STORED_METHOD
      ? await data.arrayBuffer()
      : await new Response(
          data.stream().pipeThrough(new DecompressionStream("deflate-raw")),
        ).arrayBuffer();
  return new TextDecoder().decode(bytes);
}
