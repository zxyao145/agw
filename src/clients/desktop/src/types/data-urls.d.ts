// data-urls 7 没有类型定义；这里声明本仓库使用的默认导出。data-urls 7 ships no types; this declares the default export used here.
declare module "data-urls" {
  interface MIMEType {
    readonly type: string;
    readonly subtype: string;
    readonly essence: string;
    readonly parameters: ReadonlyMap<string, string>;
    toString(): string;
  }

  /** 按 WHATWG Fetch 标准解析 `data:` URL；不能解析时为 null。Parses a `data:` URL per the WHATWG Fetch Standard; null when it cannot. */
  function parseDataURL(input: string): { mimeType: MIMEType; body: Uint8Array } | null;

  export default parseDataURL;
}
