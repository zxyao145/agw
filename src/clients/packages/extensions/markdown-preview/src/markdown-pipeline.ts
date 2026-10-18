// Markdown 渲染管线：GFM、GitHub 规则的标题 id 与代码高亮，不渲染原始 HTML。
// The Markdown pipeline: GFM, GitHub-style heading ids and code highlighting, without raw HTML.
import { defaultUrlTransform, type Options, type UrlTransform } from "react-markdown";
import rehypeHighlight from "rehype-highlight";
import rehypeSlug from "rehype-slug";
import remarkGfm from "remark-gfm";

export const remarkPlugins: Options["remarkPlugins"] = [remarkGfm];

export const rehypePlugins: Options["rehypePlugins"] = [rehypeSlug, rehypeHighlight];

/**
 * 图片地址原样交给图片组件分类（页面只显示 `data:` 与 Blob URL）；其他地址使用默认的安全转换。
 * Image sources reach the image component unchanged for classification (the page only shows `data:` and Blob URLs);
 * other URLs use the default safe transform.
 */
export const urlTransform: UrlTransform = (url, key, node) =>
  key === "src" && node.tagName === "img" ? url : defaultUrlTransform(url);
