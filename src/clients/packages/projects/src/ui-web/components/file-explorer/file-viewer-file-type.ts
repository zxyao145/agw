/**
 * 文件类型：文件名含 "." 时为 "*." 加最后一个 "." 之后的部分，否则为文件名本身；全部转为小写。
 * The file type: "*." plus the text after the last "." when the file name has one, else the whole name; lower-cased.
 */
export function fileViewerFileType(fileName: string): string {
  const dot = fileName.lastIndexOf(".");
  return (dot === -1 ? fileName : `*.${fileName.slice(dot + 1)}`).toLowerCase();
}
