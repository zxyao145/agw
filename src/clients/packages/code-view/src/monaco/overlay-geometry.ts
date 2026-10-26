// 行区域内容的横向位置（设计文档第 3 节）：从文字的左边开始，到竖向滚动条与 minimap 之前结束。纯函数，不引用 Monaco。
// The horizontal box of line-zone content (design section 3): from the left of the text to before the vertical
// scrollbar and the minimap. Pure; it does not import Monaco.

/** 计算需要的编辑器布局信息，字段与 Monaco 的 `EditorLayoutInfo` 相同。The layout fields used, as in `EditorLayoutInfo`. */
export interface ZoneLayoutInfo {
  width: number;
  contentLeft: number;
  verticalScrollbarWidth: number;
  minimap: { minimapWidth: number };
}

export interface ZoneFrameBox {
  left: number;
  width: number;
}

export function zoneFrameBox(layout: ZoneLayoutInfo): ZoneFrameBox {
  return {
    left: layout.contentLeft,
    width: Math.max(
      0,
      layout.width -
        layout.contentLeft -
        layout.verticalScrollbarWidth -
        layout.minimap.minimapWidth,
    ),
  };
}
