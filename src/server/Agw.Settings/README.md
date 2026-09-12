# Agw.Settings

Internal configuration groups for global and current-user settings. This module owns the `setting` table and `ISettingsDbContext`; other modules use `ISettingsStore`, never the table directly. Infrastructure supplies `ISettingsPersistence` and composes `AddSettings()` during Host infrastructure registration.

Each row contains `Id`, `Key`, nullable `UserId`, `ValueJson`, and an optimistic-concurrency `Version`. `UserId = null` is global; non-null values are stable authenticated user IDs. Filtered unique indexes enforce one global row per key and one row per user/key. JSON is stored as text on both SQLite and PostgreSQL.

`GetGlobalAsync<T>` and `GetCurrentUserAsync<T>` are explicit reads; they never merge or fall back. `TryCreate*` returns false if a group already exists. `TryUpdate*` requires the version returned by the read and returns false for a missing row or a conflict. It never performs an unconditional upsert. The current-user methods obtain identity from `ICurrentUser.RequiredUserId` and accept no caller-supplied user ID. Global methods are internal trusted-use-case capabilities, not authorization for a generic public settings API.

Keys are case-sensitive, 1–128 characters, with no surrounding whitespace. Values must serialize to JSON objects. Invalid stored JSON is rejected without including its contents in errors. Business modules validate their own configuration schemas. This module has no HTTP endpoints, project scope, bulk export, or history table.

`Setting` inherits `BaseEntity`. EF creation/modification interceptors maintain `CreateBy`, `CreateTime`, `UpdateBy`, and `UpdateTime`. UserId is ownership; CreateBy/UpdateBy are actors. Global edits preserve their original creator even when another trusted actor edits them. Creation metadata, key, and user scope are immutable. Updates increment Version and use tracked SaveChanges, so concurrency failures do not change stored values or audit fields.

Authentication uses only the global `auth` group. Its JSON contains `passwordHash`, `sessionVersion`, and `initializedAt`. SessionVersion is distinct from the storage Version. A user group with the same key cannot affect authentication. Password hashes and JSON values must not be logged or exposed through generic configuration APIs.

`AddSettings` directly creates the audited configuration-group table and its filtered unique indexes; rollback drops only `setting`. There is no intermediate authentication table or data-copy step. Back up settings before rollback. Apply migrations through the deployment process, never automatically as part of a normal configuration write.
