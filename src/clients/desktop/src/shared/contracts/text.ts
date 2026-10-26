/** 换行符。Line ending. */
export type TextEol = "\n" | "\r\n";

/** 主进程解码的结果：文字不含 BOM，换行符保持原样。A main-process decode result: the text has no BOM and keeps its line endings. */
export type DecodedText = {
  text: string;
  /** 编码 id，例如 `utf-8`、`gb18030`。Encoding id such as `utf-8` or `gb18030`. */
  encoding: string;
  bom: boolean;
  /** `\r\n` 多于单独的 `\n` 时为 `\r\n`。`\r\n` when it outnumbers lone `\n`. */
  eol: TextEol;
  /** 全部字节的 SHA-256，64 位小写十六进制。SHA-256 of all bytes as 64 lowercase hexadecimal digits. */
  sha256: string;
};

export type EncodeTextOptions = {
  encoding: string;
  /** 只有 `utf-8`、`utf-16le`、`utf-16be` 可以带 BOM。Only `utf-8`, `utf-16le` and `utf-16be` can have a BOM. */
  bom: boolean;
  /** 文字中的全部换行替换为它。Every line break in the text is replaced by it. */
  eol: TextEol;
};

export type EncodedText = {
  bytes: Uint8Array;
  /** `bytes` 的 SHA-256。SHA-256 of `bytes`. */
  sha256: string;
};

/** 受支持的编码，`label` 为英文名称。A supported encoding whose `label` is its English name. */
export type TextEncodingInfo = {
  id: string;
  label: string;
};
