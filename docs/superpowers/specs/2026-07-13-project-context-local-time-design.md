# Project Context Local Time Design

## Goal

Display timestamps returned by `/api/projects/{projectId}/contexts` in the browser's local time zone and locale.

## Scope

- Keep API response timestamps as ISO strings so they remain suitable for sorting and date calculations.
- Localize project-context timestamps only at the frontend display boundary.
- Apply the same parsing behavior to the project conversation list, conversation details, and chat conversation sidebar.
- Keep relative labels for Chat Contexts timestamps under 24 hours old, and display older timestamps as local `yyyy-MM-dd HH:mm:ss` values.
- Preserve the current fallback for missing and invalid values.

## Design

Add a small shared frontend date-time utility under `src/clients/web/src/lib/`. It will:

1. Parse timestamps that already contain `Z` or an explicit UTC offset without changing their meaning.
2. Treat timestamps without a time-zone suffix as UTC, matching the API's clock semantics.
3. Format valid timestamps with the browser's default locale and local time zone.
4. Format a parsed local `Date` as `yyyy-MM-dd HH:mm:ss` for the Chat Contexts fallback.
5. Return the existing fallback for missing or invalid timestamps.

The API client will continue returning raw timestamp strings. UI components that need relative labels may reuse the shared parser and retain their existing relative-label behavior.

## Data Flow

`DateTimeOffset` API value → ISO response string → shared frontend parser → browser-local `Date` → localized UI text.

## Error Handling

- Missing timestamps display `-` where the existing UI already uses that placeholder.
- Invalid timestamps preserve the original value where the existing UI currently does so.
- The chat sidebar keeps its existing relative-time labels and date-only fallback.

## Testing

Add focused frontend tests with `TZ=Asia/Singapore` covering:

- UTC timestamps ending in `Z`.
- Timestamps with an explicit numeric offset.
- UTC timestamps without a time-zone suffix.
- Exact `yyyy-MM-dd HH:mm:ss` output in the runtime local time zone.
- Missing and invalid values.

Run the focused tests first, then frontend lint, formatting checks, and build verification.

## Out of Scope

- Changing backend response contracts or serialization.
- Adding a user-selectable time-zone preference.
- Reformatting unrelated timestamps elsewhere in the application.
