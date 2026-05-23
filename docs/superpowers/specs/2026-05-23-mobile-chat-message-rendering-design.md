# Mobile Chat Message Rendering

## Goal

Update `mobile/shared/src/rn/pages/home/components/chat-panel.tsx` so mobile chat history renders messages with behavior aligned to the web `Conversation` and `AiMessageComponent` components.

## Scope

- Render text-like message contents as Markdown in React Native.
- Skip non-display messages in the same broad cases as the web conversation: missing author, `system` role, and empty contents.
- Group `FunctionCallContent` messages with matching `FunctionResultContent` messages by `additionalProperties.callId`.
- Group consecutive contents of the same type inside a message before rendering, matching the web component's streaming-friendly behavior.
- Format function call/result JSON as fenced JSON code blocks before Markdown rendering.
- Preserve the current mobile chat bubble layout and role-based left/right alignment.

## Non-Goals

- No shared package extraction between web and mobile in this change.
- No new accordion dependency. Tool groups are rendered as a compact grouped section on mobile.
- No image rendering for `UriContent`; URI content is displayed as text for now.

## Approach

Add `react-native-markdown-display` to `mobile/shared` and use it inside a new Markdown rendering component in `chat-panel.tsx`.

`ChatPanel` will call a local `processMessages` helper before rendering. This helper creates either normal items or tool-group items. Tool groups contain the function call plus matching results, and matching result indices are marked as processed so they do not render again.

`AgwMessageComponent` will render one message. It derives title and side from role and content type, groups adjacent contents by type, maps content values to display strings, and passes text-like nodes to Markdown.

## Data Handling

Content type handling mirrors the web component:

- `TextContent`, `text`, `DataContent`, `ErrorContent`, `FunctionCallContent`, and `FunctionResultContent` render through Markdown.
- `TextReasoningContent` renders through Markdown with subdued styling.
- `UsageContent` renders token and optional cost information.
- `UriContent` renders the URI as text.
- Unknown content types are ignored.

Function call/result content that looks like a JSON object is parsed and re-serialized with indentation inside a JSON code fence. Invalid JSON remains unchanged.

## Testing

Add focused Jest tests for `chat-panel.tsx`:

- Markdown source is rendered through the RN markdown component.
- `system` and missing-author messages are skipped.
- Function call and result messages with the same `callId` render in one tool group.
- Function JSON content is formatted into a fenced JSON code block.

Run the mobile test target for the new test file and then `npm run typecheck` from `mobile/shared`.
