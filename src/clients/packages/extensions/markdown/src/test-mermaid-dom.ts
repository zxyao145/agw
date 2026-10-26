// 测试用：jsdom 没有 SVG 排版，mermaid 测量文字时调用的 getBBox 与 Range 的几何方法一样返回空的结果；mermaid 生成样式时
// 使用全局的 CSSStyleSheet，图表的预览图片用全局的 XMLSerializer 输出。
// For tests: jsdom has no SVG layout, so the getBBox mermaid calls to measure text returns an empty result, as the Range
// geometry methods do; mermaid builds its styles with the global CSSStyleSheet, and diagram preview images are written
// with the global XMLSerializer.
import type { DOMWindow } from "jsdom";

export function installMermaidDom(window: DOMWindow): void {
  Object.defineProperty(window.SVGElement.prototype, "getBBox", {
    configurable: true,
    value: function getBBox() {
      return new window.DOMRect();
    },
  });
  Object.assign(globalThis, {
    CSSStyleSheet: window.CSSStyleSheet,
    XMLSerializer: window.XMLSerializer,
  });
}
