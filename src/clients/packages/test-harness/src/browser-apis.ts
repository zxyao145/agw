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

export type ObserverRegistry = {
  resize: ObservedElement[];
  intersection: ObservedElement[];
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
  const registry: ObserverRegistry = { resize: [], intersection: [] };

  window.matchMedia = (query: string) => {
    const listeners = new Set<(event: unknown) => void>();
    return {
      matches: false,
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
