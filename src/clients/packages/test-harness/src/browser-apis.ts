// Browser APIs that jsdom does not implement. Tests render real components, so the
// environment has to answer these calls the way a browser does.
// jsdom 未实现的浏览器 API。测试渲染的是真实组件，环境必须像浏览器一样回应这些调用。

type JsdomWindow = {
  [key: string]: unknown;
  navigator: Navigator;
  HTMLElement: { prototype: { [key: string]: unknown } };
  MouseEvent: new (type: string, init?: unknown) => Event;
};

export type ObservedElement = {
  target: Element;
  callback: (entries: unknown[], observer: unknown) => void;
};

export type MediaQueryRegistry = {
  /**
   * 改变媒体查询的结果，并像浏览器一样通知 `matchMedia` 的监听器；默认结果为 false。
   * Changes a media query result and notifies the `matchMedia` listeners the way a browser does; results start false.
   */
  setMatches(query: string, matches: boolean): void;
};

export type FontRegistry = {
  /**
   * 像浏览器完成一批字体加载那样，在 `document.fonts` 上派发 `loadingdone`。
   * Dispatches `loadingdone` on `document.fonts` the way a browser does after a batch of fonts loads.
   */
  loadingDone(): void;
};

export type ObserverRegistry = {
  resize: ObservedElement[];
  intersection: ObservedElement[];
  media: MediaQueryRegistry;
  fonts: FontRegistry;
};

export type Clipboard = {
  writeText(text: string): Promise<void>;
  readText(): Promise<string>;
};

/**
 * Installs the missing APIs on a jsdom window and returns the observer registry so a
 * test can deliver resize and intersection notifications itself.
 * 在 jsdom window 上安装缺失的 API，并返回观察者登记表，供测试自行投递尺寸与可见性通知。
 */
export function installBrowserApis(window: JsdomWindow): ObserverRegistry {
  const mediaMatches = new Map<string, boolean>();
  const mediaListeners = new Map<string, Set<(event: unknown) => void>>();
  // jsdom 没有 FontFaceSet；没有待加载字体的 document.fonts 是一个状态为 loaded 的事件目标。
  // jsdom has no FontFaceSet; document.fonts with no pending fonts is an event target whose status is loaded.
  const EventTargetConstructor = window.EventTarget as typeof EventTarget;
  const fonts = Object.assign(new EventTargetConstructor(), { status: "loaded" });
  Object.assign(fonts, { ready: Promise.resolve(fonts) });
  Object.defineProperty(window.document, "fonts", { configurable: true, value: fonts });
  const EventConstructor = window.Event as typeof Event;
  const registry: ObserverRegistry = {
    resize: [],
    intersection: [],
    media: {
      setMatches(query, matches) {
        mediaMatches.set(query, matches);
        for (const listener of mediaListeners.get(query) ?? []) listener({ matches, media: query });
      },
    },
    fonts: {
      loadingDone() {
        fonts.dispatchEvent(new EventConstructor("loadingdone"));
      },
    },
  };

  window.matchMedia = (query: string) => {
    const listeners = mediaListeners.get(query) ?? new Set<(event: unknown) => void>();
    mediaListeners.set(query, listeners);
    return {
      get matches() {
        return mediaMatches.get(query) ?? false;
      },
      media: query,
      onchange: null,
      addEventListener: (_type: string, listener: (event: unknown) => void) =>
        listeners.add(listener),
      removeEventListener: (_type: string, listener: (event: unknown) => void) =>
        listeners.delete(listener),
      addListener: (listener: (event: unknown) => void) => listeners.add(listener),
      removeListener: (listener: (event: unknown) => void) => listeners.delete(listener),
      dispatchEvent: () => true,
    };
  };

  window.ResizeObserver = createObserver(registry.resize);
  window.IntersectionObserver = createObserver(registry.intersection);

  // jsdom has no layout engine, so scrolling only has to move the scroll offsets.
  // jsdom 没有排版引擎，滚动只需要改变滚动偏移量。
  window.HTMLElement.prototype.scrollTo = function scrollTo(
    this: { scrollTop: number; scrollLeft: number },
    options?: { left?: number; top?: number } | number,
    top?: number,
  ) {
    if (typeof options === "number") {
      this.scrollLeft = options;
      this.scrollTop = top ?? this.scrollTop;
      return;
    }
    this.scrollLeft = options?.left ?? this.scrollLeft;
    this.scrollTop = options?.top ?? this.scrollTop;
  };
  window.HTMLElement.prototype.scrollIntoView = function scrollIntoView() {};
  // CSSOM View 的 checkVisibility：没有盒子、祖先 content-visibility: hidden、按选项检查的透明度为零与 visibility 不可见时
  // 为 false。jsdom 没有排版，content-visibility: auto 不会跳过内容。
  // CSSOM View's checkVisibility: false without a box, under a content-visibility: hidden ancestor, and, per the
  // options, at zero opacity or with an invisible visibility. jsdom has no layout, so content-visibility: auto never
  // skips contents.
  window.HTMLElement.prototype.checkVisibility = function checkVisibility(
    this: HTMLElement,
    options: {
      opacityProperty?: boolean;
      checkOpacity?: boolean;
      visibilityProperty?: boolean;
      checkVisibilityCSS?: boolean;
    } = {},
  ) {
    const view = this.ownerDocument.defaultView;
    if (!view || !this.isConnected) return false;
    const opacity = options.opacityProperty === true || options.checkOpacity === true;
    const own = view.getComputedStyle(this);
    if (own.display === "none" || own.display === "contents") return false;
    if (opacity && own.opacity === "0") return false;
    for (const ancestor of ancestorsOf(this)) {
      const style = view.getComputedStyle(ancestor);
      if (style.display === "none" || style.getPropertyValue("content-visibility") === "hidden")
        return false;
      if (opacity && style.opacity === "0") return false;
    }
    const visibility = options.visibilityProperty === true || options.checkVisibilityCSS === true;
    return !visibility || own.visibility === "visible";
  };
  window.HTMLElement.prototype.hasPointerCapture = function hasPointerCapture() {
    return false;
  };
  window.HTMLElement.prototype.setPointerCapture = function setPointerCapture() {};
  window.HTMLElement.prototype.releasePointerCapture = function releasePointerCapture() {};

  window.PointerEvent = createPointerEvent(window.MouseEvent);

  let clipboardText = "";
  const clipboard: Clipboard = {
    async writeText(text: string) {
      clipboardText = text;
    },
    async readText() {
      return clipboardText;
    },
  };
  Object.defineProperty(window.navigator, "clipboard", { configurable: true, value: clipboard });

  return registry;
}

export type LayoutMetrics = {
  /** Height reported for the scroll container. 滚动容器上报的高度。 */
  viewportHeight?: number;
  /** Height reported for each virtual row. 每个虚拟行上报的高度。 */
  rowHeight?: number;
  width?: number;
};

/**
 * Gives elements a size. jsdom performs no layout, so a virtualized list would otherwise
 * measure every element as zero and render no rows.
 * 给元素赋予尺寸。jsdom 不做排版，虚拟列表否则会把每个元素都测成零并渲染不出任何行。
 */
export function installLayoutMetrics(
  window: { HTMLElement: { prototype: object } },
  metrics: LayoutMetrics = {},
): void {
  const { viewportHeight = 600, rowHeight = 72, width = 800 } = metrics;
  Object.defineProperty(window.HTMLElement.prototype, "offsetHeight", {
    configurable: true,
    get(this: HTMLElement) {
      return this.dataset?.index === undefined ? viewportHeight : rowHeight;
    },
  });
  Object.defineProperty(window.HTMLElement.prototype, "offsetWidth", {
    configurable: true,
    get: () => width,
  });
}

/** 元素的祖先，由近到远。An element's ancestors, nearest first. */
function ancestorsOf(element: Element): Element[] {
  const ancestors: Element[] = [];
  for (let parent = element.parentElement; parent; parent = parent.parentElement) {
    ancestors.push(parent);
  }
  return ancestors;
}

function createObserver(observed: ObservedElement[]) {
  return class Observer {
    private readonly callback: (entries: unknown[], observer: unknown) => void;

    constructor(callback: (entries: unknown[], observer: unknown) => void) {
      this.callback = callback;
    }

    observe(target: Element) {
      observed.push({ target, callback: this.callback });
    }

    unobserve(target: Element) {
      const index = observed.findIndex((entry) => entry.target === target);
      if (index >= 0) observed.splice(index, 1);
    }

    disconnect() {
      for (let index = observed.length - 1; index >= 0; index -= 1) {
        if (observed[index].callback === this.callback) observed.splice(index, 1);
      }
    }

    takeRecords() {
      return [];
    }
  };
}

function createPointerEvent(MouseEventConstructor: JsdomWindow["MouseEvent"]) {
  return class PointerEvent extends MouseEventConstructor {
    readonly pointerId: number;
    readonly pointerType: string;
    readonly isPrimary: boolean;

    constructor(
      type: string,
      init: { pointerId?: number; pointerType?: string; isPrimary?: boolean } = {},
    ) {
      super(type, init);
      this.pointerId = init.pointerId ?? 1;
      this.pointerType = init.pointerType ?? "mouse";
      this.isPrimary = init.isPrimary ?? true;
    }
  };
}
