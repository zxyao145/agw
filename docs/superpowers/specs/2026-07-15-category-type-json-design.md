# CategoryType JSON Serialization Design

## Goal

Serialize and deserialize `CategoryType` values by enum member name instead of their numeric value.

## Design

Add `[JsonConverter(typeof(JsonStringEnumConverter))]` directly to the `CategoryType` enum. This keeps the behavior local to the enum and applies consistently wherever `System.Text.Json` uses it, without changing unrelated enums or relying on every property to repeat the converter.

## Verification

Add a focused test that serializes `CategoryType.GitServer` as `"GitServer"` and deserializes that string back to the same enum value.

## Scope

No global JSON option changes, API contract restructuring, migration, staging, or commit.
