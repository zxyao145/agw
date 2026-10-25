// 编辑器中行下方的区域（设计文档第 3 节），做法与 VS Code 的 ZoneWidget 相同：view zone 只占位置，内容放在 overlay
// widget 中、由我们定位的 frame 里，横向滚动时不随代码移动。
// Zones below editor lines (design section 3), built like VS Code's ZoneWidget: the view zone only reserves space, and
// the content sits in a frame we position inside an overlay widget, so it does not move with horizontal scrolling.
import type { LineZone } from "../types";
import { zoneFrameBox } from "./overlay-geometry";
import type { monaco } from "./setup";

/** 不交给 Monaco 的按键事件：评论框中的按键只由 React 处理。Key events kept from Monaco; React alone handles them. */
const ISOLATED_KEY_EVENTS = ["keydown", "keyup", "keypress"] as const;

interface ZoneEntry {
  viewZoneId: string;
  viewZone: monaco.editor.IViewZone;
  widget: monaco.editor.IOverlayWidget;
  frame: HTMLDivElement;
  container: HTMLDivElement;
  observer: ResizeObserver;
}

let nextWidgetId = 0;

export class LineZoneLayer {
  private readonly entries = new Map<string, ZoneEntry>();
  private readonly layoutListener: monaco.IDisposable;

  public constructor(private readonly editor: monaco.editor.ICodeEditor) {
    // 布局变化（含拖动 diff 分隔条）后更新 frame 的横向位置。Layout changes, diff divider drags included, move frames.
    this.layoutListener = editor.onDidLayoutChange(() => {
      for (const entry of this.entries.values()) this.placeHorizontally(entry);
    });
  }

  /**
   * 让区域与 `zones` 一致，返回每个区域中供 React portal 使用的容器。
   * Makes the zones match `zones` and returns each zone's container for a React portal.
   */
  public sync(zones: readonly LineZone[]): ReadonlyMap<string, HTMLElement> {
    const wanted = new Set(zones.map((zone) => zone.key));
    this.editor.changeViewZones((accessor) => {
      for (const [key, entry] of this.entries) {
        if (wanted.has(key)) continue;
        accessor.removeZone(entry.viewZoneId);
        this.removeWidget(entry);
        this.entries.delete(key);
      }
      for (const zone of zones) {
        const entry = this.entries.get(zone.key);
        if (entry === undefined) {
          this.entries.set(zone.key, this.create(accessor, zone));
        } else if (entry.viewZone.afterLineNumber !== zone.afterLineNumber) {
          entry.viewZone.afterLineNumber = zone.afterLineNumber;
          accessor.layoutZone(entry.viewZoneId);
        }
      }
    });
    return new Map([...this.entries].map(([key, entry]) => [key, entry.container]));
  }

  public dispose(): void {
    this.layoutListener.dispose();
    this.editor.changeViewZones((accessor) => {
      for (const entry of this.entries.values()) {
        accessor.removeZone(entry.viewZoneId);
        this.removeWidget(entry);
      }
    });
    this.entries.clear();
  }

  private create(accessor: monaco.editor.IViewZoneChangeAccessor, zone: LineZone): ZoneEntry {
    const anchor = document.createElement("div");
    anchor.className = "agw-code-view-zone-anchor";
    for (const type of ISOLATED_KEY_EVENTS) {
      anchor.addEventListener(type, (event) => event.stopPropagation());
    }
    const frame = document.createElement("div");
    frame.className =
      "agw-code-view-zone font-sans text-sm leading-normal text-foreground select-text cursor-auto whitespace-normal";
    // 第一次渲染之前先放在行的下方，自动获得焦点时编辑器不会滚动到别处。Placed below the line before the first
    // render, so autofocus does not scroll the editor elsewhere.
    frame.style.top = `${this.editor.getBottomForLineNumber(zone.afterLineNumber) - this.editor.getScrollTop()}px`;
    const container = document.createElement("div");
    frame.append(container);
    anchor.append(frame);
    const viewZone: monaco.editor.IViewZone = {
      afterLineNumber: zone.afterLineNumber,
      heightInPx: 0,
      domNode: document.createElement("div"),
      onDomNodeTop: (top) => {
        frame.style.top = `${top}px`;
      },
    };
    const widgetId = `agw.codeView.lineZone.${nextWidgetId++}`;
    const widget: monaco.editor.IOverlayWidget = {
      getId: () => widgetId,
      getDomNode: () => anchor,
      getPosition: () => null,
    };
    this.editor.addOverlayWidget(widget);
    const viewZoneId = accessor.addZone(viewZone);
    const entry: ZoneEntry = {
      viewZoneId,
      viewZone,
      widget,
      frame,
      container,
      observer: new ResizeObserver(() => this.fitHeight(entry)),
    };
    entry.observer.observe(frame);
    this.placeHorizontally(entry);
    return entry;
  }

  /** view zone 的高度跟随内容。The view zone height follows the content. */
  private fitHeight(entry: ZoneEntry): void {
    const height = entry.frame.offsetHeight;
    if (height === entry.viewZone.heightInPx) return;
    entry.viewZone.heightInPx = height;
    this.editor.changeViewZones((accessor) => accessor.layoutZone(entry.viewZoneId));
  }

  private placeHorizontally(entry: ZoneEntry): void {
    const box = zoneFrameBox(this.editor.getLayoutInfo());
    entry.frame.style.left = `${box.left}px`;
    entry.frame.style.width = `${box.width}px`;
  }

  private removeWidget(entry: ZoneEntry): void {
    entry.observer.disconnect();
    this.editor.removeOverlayWidget(entry.widget);
  }
}
