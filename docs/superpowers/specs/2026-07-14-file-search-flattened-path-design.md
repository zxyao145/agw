# Flattened Path File Search Design

## Goal

Make `GET /api/files/search` search the flattened relative paths of files and directories instead of matching only each entry's basename.

## Search Semantics

- Normalize returned relative paths to `/` separators on every platform.
- Represent directories with a trailing `/`, for example `demo/abc/`.
- Represent files without a trailing separator, for example `demo/abc/d.txt`.
- Match the keyword against the complete normalized relative path using an ordinal, case-insensitive substring comparison.
- Therefore `a` matches `demo/abc/`, `demo/abc/d.txt`, and `demo/abc/e.log`.
- Therefore `abc/` matches the directory `demo/abc/` and its descendants.
- Recursive search considers the complete permitted tree; non-recursive search considers direct children only.

## Existing Behavior Preserved

- Hidden directories and configured ignored directories remain excluded.
- Ignored-file filtering remains unchanged.
- Directories remain sorted before files, followed by relative path.
- The existing result limit and response contract remain unchanged.

## Tests

Controller tests cover a plain segment query (`a`), a separator-bearing query (`abc/`), and non-recursive direct-directory behavior.
