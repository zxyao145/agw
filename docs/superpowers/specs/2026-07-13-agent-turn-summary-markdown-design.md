# Agent Turn Summary Markdown Design

## Goal

Allow `AgentTurnSummaryService` to return Markdown when formatting improves the readability of a turn summary.

## Behavior

- Keep the existing result message contract: System role, `$agw-server` author, `type=result`, and exactly one `TextContent`.
- Update the default summary instructions to permit Markdown constructs such as lists, emphasis, headings, and code blocks when useful.
- Do not require Markdown when plain text is clearer. Plain text remains a valid result.
- Continue asking the model to return only the summary, without JSON, XML, wrapper objects, or transport metadata.
- Preserve the model's Markdown text as returned, apart from the existing leading and trailing whitespace trim.
- Apply the behavior to both Definition Agent summaries and Agentflow Output summaries because they share `AgentTurnSummaryService`.

## Scope

This change only updates the summary instructions, regression tests, and execution documentation. It does not add API fields, database columns, message content types, Markdown post-processing, or per-agent format configuration.

## Verification

- Verify that the generated system prompt explicitly allows optional Markdown.
- Verify that a Markdown response is returned and persisted unchanged except for surrounding whitespace.
- Run the `Agw.Agents.Tests` project and the normal backend build checks.
