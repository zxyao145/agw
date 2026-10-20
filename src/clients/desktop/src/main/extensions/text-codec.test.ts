import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";

import iconv from "iconv-lite";

import { toExtensionIpcResult } from "./extension-ipc-result";
import { decodeText, encodeText, listTextEncodings } from "./text-codec";

const sha256 = (bytes: Uint8Array) => createHash("sha256").update(bytes).digest("hex");

const ENGLISH = "Hello, world.\nThe quick brown fox jumps over the lazy dog.\n";
const CHINESE =
  "春眠不觉晓，处处闻啼鸟。山中何所有，岭上多白云。床前明月光，疑是地上霜。举头望明月，低头思故乡。\n" +
  "白日依山尽，黄河入海流。欲穷千里目，更上一层楼。中华人民共和国是一个历史悠久的国家，文化源远流长。\n";
const JAPANESE =
  "吾輩は猫である。名前はまだ無い。どこで生れたかとんと見当がつかぬ。何でも薄暗いじめじめした所でニャーニャー泣いていた事だけは記憶している。\n" +
  "吾輩はここで始めて人間というものを見た。しかもあとで聞くとそれは書生という人間中で一番獰悪な種族であったそうだ。\n";
const FRENCH =
  "Le café « déjà vu » coûte 3 € — c’est très « chic ». L’été dernier, nous étions à Noël près de la forêt.\n" +
  "Où est la bibliothèque ? Ça dépend… “Smart quotes” and naïve façade.\n";
const RUSSIAN = "Съешь же ещё этих мягких французских булок, да выпей чаю.\n";

test("the encoding list is the fixed list with English labels", () => {
  const encodings = listTextEncodings();
  assert.deepEqual(
    encodings.map((encoding) => encoding.id),
    [
      "utf-8",
      "utf-16le",
      "utf-16be",
      "gb18030",
      "gbk",
      "big5",
      "big5-hkscs",
      "shift_jis",
      "euc-jp",
      "euc-kr",
      "windows-1250",
      "windows-1251",
      "windows-1252",
      "windows-1253",
      "windows-1254",
      "windows-1255",
      "windows-1256",
      "windows-1257",
      "windows-1258",
      "iso-8859-1",
      "iso-8859-2",
      "iso-8859-3",
      "iso-8859-4",
      "iso-8859-5",
      "iso-8859-6",
      "iso-8859-7",
      "iso-8859-8",
      "iso-8859-9",
      "iso-8859-10",
      "iso-8859-11",
      "iso-8859-13",
      "iso-8859-14",
      "iso-8859-15",
      "iso-8859-16",
      "koi8-r",
      "koi8-u",
      "macintosh",
      "ibm866",
    ],
  );
  assert.equal(
    encodings.find((encoding) => encoding.id === "gb18030")?.label,
    "Chinese Simplified (GB18030)",
  );
  for (const { id, label } of encodings) {
    assert.match(label, /^[A-Za-z][ -~]+$/u, id);
    const written = encodeText("Plain text 123\n", { encoding: id, bom: false, eol: "\n" });
    assert.equal(decodeText(written.bytes, id).text, "Plain text 123\n", id);
  }
});

test("UTF-8 without a BOM is detected and the hash matches node:crypto", () => {
  const bytes = iconv.encode(`${ENGLISH}${CHINESE}`, "utf-8");
  assert.deepEqual(decodeText(bytes), {
    text: `${ENGLISH}${CHINESE}`,
    encoding: "utf-8",
    bom: false,
    eol: "\n",
    sha256: sha256(bytes),
  });
});

test("a BOM identifies UTF-8, UTF-16LE and UTF-16BE and is not part of the text", () => {
  for (const encoding of ["utf-8", "utf-16le", "utf-16be"]) {
    const bytes = iconv.encode(`${ENGLISH}${CHINESE}`, encoding, { addBOM: true });
    assert.deepEqual(
      decodeText(bytes),
      {
        text: `${ENGLISH}${CHINESE}`,
        encoding,
        bom: true,
        eol: "\n",
        sha256: sha256(bytes),
      },
      encoding,
    );
  }
});

test("UTF-16LE and UTF-16BE without a BOM are detected by the position of NUL bytes", () => {
  const chineseMarkdown = `# 第 1 章\n\n${CHINESE}`;
  for (const encoding of ["utf-16le", "utf-16be"]) {
    for (const text of [ENGLISH, JAPANESE, chineseMarkdown]) {
      const bytes = iconv.encode(text, encoding);
      const expected = { text, encoding, bom: false, eol: "\n", sha256: sha256(bytes) };
      assert.deepEqual(decodeText(bytes), expected, `${encoding}: ${text.slice(0, 8)}`);
      assert.deepEqual(decodeText(bytes, encoding), expected, `${encoding}: ${text.slice(0, 8)}`);
    }
  }
});

test("BOM-less UTF-16 with as many NUL bytes at odd offsets as at even offsets is read as UTF-16LE", () => {
  const bytes = iconv.encode(CHINESE, "utf-16le");
  const nulOffsets = [...bytes.entries()]
    .filter(([, value]) => value === 0)
    .map(([index]) => index);
  const odd = nulOffsets.filter((index) => index % 2 === 1).length;
  assert.ok(odd > 0);
  assert.equal(odd, nulOffsets.length - odd);
  assert.deepEqual(decodeText(bytes), {
    text: CHINESE,
    encoding: "utf-16le",
    bom: false,
    eol: "\n",
    sha256: sha256(bytes),
  });
});

test("GB18030, Shift_JIS and windows-1252 are detected", () => {
  for (const [encoding, text] of [
    ["gb18030", CHINESE],
    ["shift_jis", JAPANESE],
    ["windows-1252", FRENCH],
  ] as const) {
    const bytes = iconv.encode(text, encoding);
    assert.deepEqual(
      decodeText(bytes),
      { text, encoding, bom: false, eol: "\n", sha256: sha256(bytes) },
      encoding,
    );
  }
});

test("line endings: CRLF, LF and mixed files keep their text and report the majority", () => {
  const cases: Array<[string, "\n" | "\r\n"]> = [
    ["a\r\nb\r\n", "\r\n"],
    ["a\nb\n", "\n"],
    ["a\r\nb\r\nc\n", "\r\n"],
    ["a\r\nb\nc\n", "\n"],
    ["a\r\nb\n", "\n"],
    ["a\rb\r", "\n"],
    ["", "\n"],
  ];
  for (const [text, eol] of cases) {
    const result = decodeText(iconv.encode(text, "utf-8"));
    assert.equal(result.text, text, JSON.stringify(text));
    assert.equal(result.eol, eol, JSON.stringify(text));
  }
  assert.equal(decodeText(iconv.encode("一\r\n二\r\n三\n", "gb18030")).eol, "\r\n");
});

test("an explicit encoding is used as given and keeps its BOM out of the text", () => {
  const gbkBytes = iconv.encode(CHINESE, "gb18030");
  assert.deepEqual(decodeText(gbkBytes, "gbk"), {
    text: CHINESE,
    encoding: "gbk",
    bom: false,
    eol: "\n",
    sha256: sha256(gbkBytes),
  });
  const cyrillic = iconv.encode(RUSSIAN, "windows-1251");
  assert.equal(decodeText(cyrillic, "windows-1251").text, RUSSIAN);
  const withBom = iconv.encode(ENGLISH, "utf-8", { addBOM: true });
  assert.deepEqual(decodeText(withBom, "utf-8"), {
    text: ENGLISH,
    encoding: "utf-8",
    bom: true,
    eol: "\n",
    sha256: sha256(withBom),
  });
});

test("an explicit encoding that cannot read the bytes losslessly is ERR_UNKNOWN_ENCODING", () => {
  assert.throws(() => decodeText(iconv.encode(CHINESE, "gb18030"), "utf-8"), {
    code: "ERR_UNKNOWN_ENCODING",
  });
  assert.throws(() => decodeText(iconv.encode(JAPANESE, "shift_jis"), "euc-kr"), {
    code: "ERR_UNKNOWN_ENCODING",
  });
  // ISO-8859-1 能映射每个字节，但 0x85 解码为 C1 控制字符。ISO-8859-1 maps every byte, yet 0x85 decodes to a C1 control.
  assert.throws(() => decodeText(iconv.encode("dots…", "windows-1252"), "iso-8859-1"), {
    code: "ERR_UNKNOWN_ENCODING",
  });
});

test("an explicit encoding outside the list is ERR_UNSUPPORTED_ENCODING", () => {
  assert.throws(() => decodeText(iconv.encode(ENGLISH, "utf-8"), "utf-32le"), {
    code: "ERR_UNSUPPORTED_ENCODING",
  });
});

test("the check rejects control characters and U+FFFD and allows tab, line breaks, form feed, vertical tab and ESC", () => {
  const allowed = "a\tb\nc\rd\fe\vf\u001bg";
  assert.equal(decodeText(iconv.encode(allowed, "utf-8"), "utf-8").text, allowed);
  for (const rejected of ["bell\u0007", "delete\u007f", "next\u0085line", "replaced�"]) {
    assert.throws(
      () => decodeText(iconv.encode(rejected, "utf-8"), "utf-8"),
      { code: "ERR_UNKNOWN_ENCODING" },
      JSON.stringify(rejected),
    );
  }
});

test("bytes with NUL that no encoding reads losslessly are ERR_NOT_TEXT", () => {
  const binary = Buffer.concat([
    iconv.encode("PK", "utf-8"),
    Buffer.from([3, 4, 0, 0, 20, 0, 8, 0]),
  ]);
  assert.throws(() => decodeText(binary), { code: "ERR_NOT_TEXT" });
  // UTF-16LE 的 BOM 之后字节数为奇数，不能无损解码。An odd byte count after a UTF-16LE BOM cannot be decoded losslessly.
  const oddUtf16 = iconv.encode("AB", "utf-16le", { addBOM: true }).subarray(0, 5);
  assert.throws(() => decodeText(oddUtf16), { code: "ERR_NOT_TEXT" });
  // 偶数长度、含有 NUL 的二进制同样不能按 UTF-16 解码。Even-length binary with NUL does not decode as UTF-16 either.
  const evenBinary = Buffer.concat([binary, Buffer.from([0x00, 0x00, 0xff, 0x01])]);
  assert.equal(evenBinary.length % 2, 0);
  assert.throws(() => decodeText(evenBinary), { code: "ERR_NOT_TEXT" });
});

test("a BOM decides the only candidate, and failing the check ends detection", () => {
  const utf8BomInvalid = Buffer.concat([
    iconv.encode("abc", "utf-8", { addBOM: true }),
    Buffer.from([0xc3, 0x28]),
  ]);
  assert.throws(() => decodeText(utf8BomInvalid), { code: "ERR_UNKNOWN_ENCODING" });
});

test("bytes without NUL that no encoding reads losslessly are ERR_UNKNOWN_ENCODING", () => {
  const undetectable = Buffer.concat([iconv.encode("abc", "utf-8"), Buffer.from([0x01, 0xc3])]);
  assert.throws(() => decodeText(undetectable), { code: "ERR_UNKNOWN_ENCODING" });
});

test("a multi-byte character cut by the end of the first 64 KiB is still decoded", () => {
  for (const [encoding, text] of [
    ["gb18030", `a${CHINESE.repeat(400)}`],
    ["shift_jis", `a${"猫".repeat(40_000)}`],
    ["utf-8", `ab${"中".repeat(30_000)}`],
  ] as const) {
    const bytes = iconv.encode(text, encoding);
    // 前 64 KiB 的最后一个字符不完整。The last character of the first 64 KiB is incomplete.
    assert.match(iconv.decode(bytes.subarray(0, 64 * 1024), encoding), /�$/u, encoding);
    assert.equal(decodeText(bytes, encoding).text, text, encoding);
  }
  assert.equal(decodeText(iconv.encode(`a${CHINESE.repeat(400)}`, "gb18030")).encoding, "gb18030");
});

test("a rejected character after the first 64 KiB still fails the check, and large binary is ERR_NOT_TEXT", () => {
  const late = iconv.encode(`${FRENCH.repeat(500)}bell\u0007`, "windows-1252");
  assert.ok(late.length > 64 * 1024);
  assert.throws(() => decodeText(late, "windows-1252"), { code: "ERR_UNKNOWN_ENCODING" });
  const binary = Buffer.concat([iconv.encode("PK", "utf-8"), Buffer.alloc(1024 * 1024)]);
  assert.throws(() => decodeText(binary), { code: "ERR_NOT_TEXT" });
});

test("writing back with the read encoding, BOM and line ending reproduces the bytes", () => {
  const files = [
    iconv.encode(ENGLISH, "utf-8"),
    iconv.encode(CHINESE.replaceAll("\n", "\r\n"), "utf-8", { addBOM: true }),
    iconv.encode(CHINESE, "utf-16le", { addBOM: true }),
    iconv.encode(JAPANESE, "utf-16be", { addBOM: true }),
    iconv.encode(CHINESE.replaceAll("\n", "\r\n"), "gb18030"),
    iconv.encode(JAPANESE, "shift_jis"),
    iconv.encode(FRENCH, "windows-1252"),
  ];
  for (const bytes of files) {
    const { text, encoding, bom, eol, sha256: hash } = decodeText(bytes);
    const written = encodeText(text, { encoding, bom, eol });
    assert.deepEqual(Buffer.from(written.bytes), bytes, encoding);
    assert.equal(written.sha256, hash, encoding);
  }
});

test("every line break is replaced by the requested line ending", () => {
  const mixed = "a\r\nb\rc\nd";
  assert.equal(
    Buffer.from(encodeText(mixed, { encoding: "utf-8", bom: false, eol: "\r\n" }).bytes).toString(
      "utf8",
    ),
    "a\r\nb\r\nc\r\nd",
  );
  assert.equal(
    Buffer.from(encodeText(mixed, { encoding: "utf-8", bom: false, eol: "\n" }).bytes).toString(
      "utf8",
    ),
    "a\nb\nc\nd",
  );
});

test("a BOM is written for UTF encodings and rejected for others", () => {
  const boms: Array<[string, number[]]> = [
    ["utf-8", [0xef, 0xbb, 0xbf]],
    ["utf-16le", [0xff, 0xfe]],
    ["utf-16be", [0xfe, 0xff]],
  ];
  for (const [encoding, bom] of boms) {
    const written = encodeText("A", { encoding, bom: true, eol: "\n" });
    assert.deepEqual(
      Buffer.from(written.bytes),
      Buffer.concat([Buffer.from(bom), iconv.encode("A", encoding)]),
      encoding,
    );
    assert.equal(written.sha256, sha256(written.bytes), encoding);
  }
  assert.throws(() => encodeText("A", { encoding: "gb18030", bom: true, eol: "\n" }), {
    code: "ERR_INVALID_ARGUMENT",
  });
});

test("a character the encoding cannot represent is ERR_UNENCODABLE with its line and column", () => {
  assert.throws(
    () => encodeText("first line\r\nab😀cd", { encoding: "gbk", bom: false, eol: "\r\n" }),
    {
      code: "ERR_UNENCODABLE",
      message: /"😀" \(U\+1F600\) at line 2, column 3 /u,
    },
  );
  assert.throws(() => encodeText("x\ny\n€", { encoding: "shift_jis", bom: false, eol: "\n" }), {
    code: "ERR_UNENCODABLE",
    message: /"€" \(U\+20AC\) at line 3, column 1 /u,
  });
  assert.throws(() => encodeText("ok\ud800", { encoding: "utf-8", bom: false, eol: "\n" }), {
    code: "ERR_UNENCODABLE",
    message: /\(U\+D800\) at line 1, column 3 /u,
  });
  // GB18030 能表示全部 Unicode 字符，表情字符按四字节序列写出。GB18030 covers all of Unicode; emoji are written as four-byte sequences.
  const emoji = encodeText("ab😀cd", { encoding: "gb18030", bom: false, eol: "\n" });
  assert.equal(decodeText(emoji.bytes, "gb18030").text, "ab😀cd");
});

test("encoding arguments are checked", () => {
  assert.throws(() => encodeText("A", { encoding: "utf-32le", bom: false, eol: "\n" }), {
    code: "ERR_UNSUPPORTED_ENCODING",
  });
  assert.throws(
    () => encodeText("A", { encoding: "utf-8", bom: false, eol: "\r" as unknown as "\n" }),
    { code: "ERR_INVALID_ARGUMENT" },
  );
  assert.throws(
    () => encodeText("A", { encoding: "utf-8", bom: "yes" as unknown as boolean, eol: "\n" }),
    { code: "ERR_INVALID_ARGUMENT" },
  );
  assert.throws(
    () => encodeText(42 as unknown as string, { encoding: "utf-8", bom: false, eol: "\n" }),
    {
      code: "ERR_INVALID_ARGUMENT",
    },
  );
  assert.throws(() => decodeText("text" as unknown as Uint8Array), {
    code: "ERR_INVALID_ARGUMENT",
  });
});

test("codec results and error codes survive the IPC result wrapper", async () => {
  const failure = structuredClone(
    await toExtensionIpcResult(async () =>
      decodeText(Buffer.from([0x50, 0x4b, 0x03, 0x04, 0x00, 0x00, 0x14, 0x00])),
    ),
  );
  assert.equal(failure.ok, false);
  if (!failure.ok) {
    assert.equal(failure.error.code, "ERR_NOT_TEXT");
    assert.equal(failure.error.name, "ExtensionError");
  }
  const success = structuredClone(
    await toExtensionIpcResult(async () =>
      encodeText("A\n", { encoding: "utf-16le", bom: true, eol: "\r\n" }),
    ),
  );
  assert.equal(success.ok, true);
  if (success.ok) {
    assert.ok(success.value.bytes instanceof Uint8Array);
    assert.deepEqual([...success.value.bytes], [0xff, 0xfe, 0x41, 0x00, 0x0d, 0x00, 0x0a, 0x00]);
    assert.equal(success.value.sha256, sha256(success.value.bytes));
  }
});
