# Empty Message Persistence Design

## Goal

Prevent chat messages with no meaningful content from creating database records.

## Persistence Boundary

Filter messages in `EfCoreChatHistoryProvider.AppendAsync` before any project-context or task-record database work. This is the shared write boundary used by both framework chat-history saves and direct `IConversationHistoryWriter` calls.

A message is empty when it has no content items, or when every content item is a `TextContent` whose text is null, empty, or whitespace. Any non-text content item makes the message persistable so function calls, function results, approvals, attachments, and other protocol content remain intact.

If every supplied message is empty, return without creating or updating a `ProjectContext`. For mixed batches, derive the title, task record sequence, and metadata only from the filtered messages.

## Tests

Add a focused `EfCoreChatHistoryProviderTests` regression test that passes blank text messages alongside a non-text function-call message. The existing implementation must fail by persisting all messages; the fixed implementation must persist only the function-call message.

Run the focused test, the full `Agw.Projects.Tests` project, and the repository-wide backend test suite. No migration, API contract, frontend, staging, or commit changes are included.
