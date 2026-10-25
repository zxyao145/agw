// code-view 的浏览器测试页：测试通过 `window.fixture.set` 改变查看器的属性，通过 `window.fixture.calls` 读取回调；
// 可编辑模式下 `window.fixture.text` 像宿主的文档会话一样读入文字并读取模型。
// The code-view browser test page: tests change the viewer props through `window.fixture.set` and read callbacks
// through `window.fixture.calls`; in editable mode `window.fixture.text` loads text and reads the model the way the
// host's document session does.
import "../../src/code-view.css";
import "./fixture.css";

import * as React from "react";
import { createRoot } from "react-dom/client";

import {
  CodeDiffViewer,
  CodeViewAppearanceProvider,
  CodeViewer,
  type CodeEol,
  type CodeTextModel,
  type LineZone,
  remapLine,
} from "../../src/index";
import { loadCodeTextModel } from "../../src/text-model";

export interface FixtureState {
  mode: "source" | "editable" | "diff";
  revision: number;
  card: boolean;
  width: number | null;
  wordWrap: boolean;
  /** 可编辑时文档已读入且没有失败。The editable document is loaded and not failed. */
  shown: boolean;
  filePath: string;
  text: string;
  original: string;
  modified: string;
  /** 单个编辑器或修改侧的行区域。Zones of the single editor or the modified side. */
  zones: number[];
  originalZones: number[];
  /** 不能执行行操作的行。Lines where the line action cannot run. */
  blocked: number[];
  /** 行区域内容下方额外的高度。Extra height below the zone content. */
  zoneHeight: number;
  autoFocusZone: number | null;
}

declare global {
  interface Window {
    fixture: {
      set(update: Partial<FixtureState>): void;
      calls: unknown[][];
      /** 编辑器提供的格式化函数；没有时为 null。The editor's format function, or null. */
      format: (() => void) | null;
      text: {
        load(text: string, eol: CodeEol, name: string): Promise<void>;
        value(): string;
        eol(): CodeEol;
        snapshot(): number;
        matches(snapshot: number): boolean;
      };
    };
  }
}

const lines = (count: number, line: (index: number) => string) =>
  Array.from({ length: count }, (_, index) => line(index + 1)).join("\n");

const original = lines(40, (index) => `const a${index} = ${index};`);
const modified = original
  .replace("const a20 = 20;", "const a20 = 200;")
  .replace("const a21 = 21;", "const a21 = 21;\nconst extra = 1;");

let state: FixtureState = {
  mode: "source",
  revision: 0,
  card: false,
  width: null,
  wordWrap: false,
  shown: true,
  filePath: "src/sample.ts",
  text: lines(60, (index) => `export const value${index} = ${index};`),
  original,
  modified,
  zones: [],
  originalZones: [],
  blocked: [],
  zoneHeight: 0,
  autoFocusZone: null,
};
const listeners = new Set<() => void>();
let textModel: CodeTextModel | null = null;

function update(next: Partial<FixtureState>): void {
  state = { ...state, ...next };
  for (const listener of listeners) listener();
}

function requireModel(): CodeTextModel {
  if (textModel === null) throw new Error("No text was loaded.");
  return textModel;
}

window.fixture = {
  calls: [],
  format: null,
  set: update,
  text: {
    async load(text, eol, name) {
      textModel ??= await loadCodeTextModel();
      textModel.load({ text, eol }, { uri: `file:///project/${name}`, name });
      update({ mode: "editable", filePath: name, revision: state.revision });
    },
    value: () => requireModel().serialize(),
    eol: () => requireModel().eol(),
    snapshot: () => requireModel().snapshot(),
    matches: (snapshot) => requireModel().matches(snapshot),
  },
};

function useFixtureState(): FixtureState {
  return React.useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    () => state,
  );
}

function ZoneContent({ zone, side }: { zone: LineZone; side: string }) {
  const current = useFixtureState();
  const line = zone.afterLineNumber;
  return (
    <div data-zone={`${side}:${line}`} style={{ padding: 4, background: "#dbeafe" }}>
      <textarea
        aria-label={`note ${side} ${zone.key}`}
        autoFocus={current.autoFocusZone === line}
        onKeyDown={(event) =>
          window.fixture.calls.push([
            "key",
            side,
            line,
            `${event.ctrlKey ? "Control+" : ""}${event.metaKey ? "Meta+" : ""}${event.key}`,
          ])
        }
      />
      <div style={{ height: current.zoneHeight }} />
    </div>
  );
}

/** 行区域的 key 按位置编号，行号变化时不变。Zone keys number the zones, so they stay when lines move. */
function sideProps(side: string, zoneLines: readonly number[], current: FixtureState) {
  return {
    lineZones: zoneLines.map((line, index) => ({ key: `zone:${index}`, afterLineNumber: line })),
    markedLines: zoneLines,
    renderLineZone: (zone: LineZone) => <ZoneContent zone={zone} side={side} />,
    lineZoneAction: {
      label: "Add note",
      canRun: (line: number) => !current.blocked.includes(line),
      run: (line: number) => window.fixture.calls.push(["run", side, line]),
    },
  };
}

function App() {
  const current = useFixtureState();
  let body: React.ReactNode;
  if (current.mode === "diff") {
    body = (
      <CodeDiffViewer
        filePath="src/sample.ts"
        original={{
          text: current.original,
          label: "Before",
          present: true,
          ...sideProps("original", current.originalZones, current),
        }}
        modified={{
          text: current.modified,
          label: "After",
          present: true,
          ...sideProps("modified", current.zones, current),
        }}
      />
    );
  } else if (current.mode === "editable") {
    body = (
      <CodeViewer
        filePath={current.filePath}
        ariaLabel="Sample"
        wordWrap={current.wordWrap}
        content={{
          kind: "editable",
          model: requireModel(),
          shown: current.shown,
          encoding: <span data-encoding-slot>UTF-8</span>,
          onFormatChange: (format) => {
            window.fixture.format = format;
            window.fixture.calls.push(["format", format !== null]);
          },
          onError: (message) => window.fixture.calls.push(["error", message]),
          onDidChangeLines: (changes) => {
            window.fixture.calls.push(["lines", changes]);
            // 与 @agw/projects 相同，行区域跟随编辑移动。Zones follow the edits, as in @agw/projects.
            update({ zones: state.zones.map((line) => remapLine(line, changes)) });
          },
        }}
        {...sideProps("current", current.zones, current)}
      />
    );
  } else {
    body = (
      <CodeViewer
        filePath={current.filePath}
        ariaLabel="Sample"
        wordWrap={current.wordWrap}
        content={{ kind: "text", text: current.text }}
        {...sideProps("current", current.zones, current)}
      />
    );
  }
  return (
    <CodeViewAppearanceProvider revision={current.revision}>
      <div className="stage">
        <div
          data-host
          className={`${current.card ? "card " : ""}flex min-h-0 flex-1 flex-col`}
          style={{ width: current.width ?? undefined }}
        >
          {body}
        </div>
      </div>
    </CodeViewAppearanceProvider>
  );
}

createRoot(document.getElementById("root")!).render(<App />);
