import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import ts from "typescript";

const CONVERSATION_URL = new URL("./conversation.tsx", import.meta.url);

async function loadMessageProcessor() {
  const source = await readFile(CONVERSATION_URL, "utf8");
  const start = source.indexOf("const defaultProcessMessages");
  const end = source.indexOf("\nexport function Conversation");
  const processorSource = source
    .slice(start, end)
    .replace("const defaultProcessMessages", "export const defaultProcessMessages");
  const javascript = ts.transpileModule(
    `
const MessageContentType = {
  FunctionCallContent: "FunctionCallContent",
  FunctionResultContent: "FunctionResultContent",
};
const isResultMessage = (message) => message.additionalProperties?.type === "result";
${processorSource}
`,
    {
      compilerOptions: {
        module: ts.ModuleKind.ES2022,
        target: ts.ScriptTarget.ES2022,
      },
    },
  ).outputText;

  return import(`data:text/javascript;base64,${Buffer.from(javascript).toString("base64")}`);
}

function toolMessage(type: string, scope: string, callId = "item_1") {
  return toolContentsMessage(type, scope, [callId]);
}

function toolContentsMessage(type: string, scope: string, callIds: string[]) {
  return {
    messageId: `${type}-${scope}`,
    author: "agent",
    role: type === "FunctionCallContent" ? "assistant" : "tool",
    streamingScopeId: scope,
    contents: callIds.map((callId) => ({
      type,
      content: `${type}-${callId}`,
      additionalProperties: {
        callId,
        ...(type === "FunctionCallContent" ? { toolName: `tool-${callId}` } : {}),
      },
    })),
  };
}

test("conversation renders agent name and author metadata above agent messages", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /function getMessageMeta/);
  assert.match(source, /agentName/);
  assert.match(source, /agentAuthor/);
  assert.match(source, /\{messageMeta\.name\}/);
  assert.match(source, /\{messageMeta\.author\}/);
  assert.match(source, /AiMessageComponent message=\{item\.message\}/);
});

test("conversation renders user author metadata above and aligned with user messages", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.doesNotMatch(source, /message\.role === "user" \|\| isResultMessage\(message\)/);
  assert.match(source, /if \(message\.role === "user"\)[\s\S]*?name: null,[\s\S]*?author:/);
  assert.match(source, /const isUserMessage = item\.message\.role === "user";/);
  assert.match(source, /isUserMessage \? "ml-auto justify-end" : ""/);
});

test("conversation can delegate scrolling while keeping messages centered", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /scrollable\?: boolean;/);
  assert.match(source, /scrollable = true,/);
  assert.match(source, /scrollable && "overflow-y-auto agw-scrollbar"/);
  assert.match(source, /<div className="mx-auto w-full max-w-225 space-y-4 pb-36">/);
});

test("conversation embeds a pending human interaction in its matching function call", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /matchesHumanInteractionCall\(item\.message, pendingHumanInteraction\)/);
  assert.match(source, /data-function-call-id=\{pendingHumanInteraction\.callId\}/);
  assert.match(source, /<HumanInteractionPanel[\s\S]*?embedded/);
});

test("conversation renders completed ask_user_question calls as question and answer text", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /item\.toolName === "ask_user_question"/);
  assert.match(source, /getHumanInteractionQuestionResult\(item\.messages\)/);
  assert.match(source, /<HumanInteractionQuestionResultView result=\{questionResult\}/);
});

test("conversation renders authorless system messages while hiding injected user messages", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /collapseConsecutiveSystemMessages\(messages\)/);
  assert.match(source, /message\.role === "user" && !message\.author/);
  assert.doesNotMatch(source, /if \(message\.role === "system"\) \{\s*continue;/);
  assert.match(source, /if \(isResultMessage\(message\)\)[\s\S]*?continue;/);
});

test("conversation restores persisted authorless assistant and tool messages", async () => {
  const { defaultProcessMessages } = await loadMessageProcessor();
  const items = defaultProcessMessages([
    {
      messageId: "assistant-1",
      author: null,
      role: "assistant",
      streamingScopeId: "user-1",
      contents: [
        { type: "TextReasoningContent", content: "Planning the task" },
        {
          type: "FunctionCallContent",
          content: "{}",
          additionalProperties: { callId: "call-1", toolName: "todos_add" },
        },
      ],
    },
    {
      messageId: "",
      author: null,
      role: "tool",
      streamingScopeId: "user-1",
      contents: [
        {
          type: "FunctionResultContent",
          content: "[]",
          additionalProperties: { callId: "call-1" },
        },
      ],
    },
  ]);

  assert.deepEqual(
    items.map((item: { type: string }) => item.type),
    ["normal", "accordion"],
  );
});

test("duplicate call ids produce one tool group per turn", async () => {
  const { defaultProcessMessages } = await loadMessageProcessor();
  const items = defaultProcessMessages([
    toolMessage("FunctionCallContent", "user-1"),
    toolMessage("FunctionResultContent", "user-1"),
    toolMessage("FunctionCallContent", "user-2"),
    toolMessage("FunctionResultContent", "user-2"),
  ]);

  assert.equal(items.length, 2);
  assert.deepEqual(
    items.map((item: { type: string }) => item.type),
    ["accordion", "accordion"],
  );
  assert.deepEqual(
    items.map((item: { messages: Array<{ streamingScopeId: string }> }) =>
      item.messages.map((message) => message.streamingScopeId),
    ),
    [
      ["user-1", "user-1"],
      ["user-2", "user-2"],
    ],
  );
});

test("concurrent tool calls pair with out-of-order results in call order", async () => {
  const { defaultProcessMessages } = await loadMessageProcessor();
  const items = defaultProcessMessages([
    toolContentsMessage("FunctionCallContent", "user-1", ["call-1", "call-2", "call-3"]),
    toolContentsMessage("FunctionResultContent", "user-1", ["call-3", "call-1", "call-2"]),
  ]);

  assert.deepEqual(
    items.map((item: { type: string }) => item.type),
    ["accordion", "accordion", "accordion"],
  );
  assert.deepEqual(
    items.map((item: { toolName: string }) => item.toolName),
    ["tool-call-1", "tool-call-2", "tool-call-3"],
  );
  assert.deepEqual(
    items.map((item: { messages: Array<{ contents: Array<{ additionalProperties: unknown }> }> }) =>
      item.messages.map(
        (message) => (message.contents[0].additionalProperties as { callId: string }).callId,
      ),
    ),
    [
      ["call-1", "call-1"],
      ["call-2", "call-2"],
      ["call-3", "call-3"],
    ],
  );
});

test("final result messages keep their result classification", async () => {
  const { defaultProcessMessages } = await loadMessageProcessor();
  const finalResult = {
    messageId: "final-result",
    author: "agent",
    role: "assistant",
    contents: [{ type: "TextContent", content: "done" }],
    additionalProperties: { type: "result" },
  };

  const items = defaultProcessMessages([finalResult]);

  assert.deepEqual(items, [{ type: "result", message: finalResult }]);
});

test("mixed ordinary and unmatched tool contents preserve content order", async () => {
  const { defaultProcessMessages } = await loadMessageProcessor();
  const message = {
    messageId: "mixed-message",
    author: "agent",
    role: "assistant",
    streamingScopeId: "user-1",
    contents: [
      { type: "TextContent", content: "before" },
      {
        type: "FunctionCallContent",
        content: "call",
        additionalProperties: { callId: "call-without-result", toolName: "orphan-call" },
      },
      { type: "TextContent", content: "after" },
      {
        type: "FunctionResultContent",
        content: "result",
        additionalProperties: { callId: "result-without-call" },
      },
    ],
  };

  const items = defaultProcessMessages([message]);

  assert.deepEqual(
    items.map((item: { type: string }) => item.type),
    ["normal", "normal", "normal", "normal"],
  );
  assert.deepEqual(
    items.map((item: { message: { contents: Array<{ type: string }> } }) =>
      item.message.contents.map((content) => content.type),
    ),
    [["TextContent"], ["FunctionCallContent"], ["TextContent"], ["FunctionResultContent"]],
  );
});
