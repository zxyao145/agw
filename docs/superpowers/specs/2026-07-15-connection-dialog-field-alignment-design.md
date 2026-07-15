# Connection Dialog Field Alignment Design

## Problem

The Create/Edit connection dialog renders Display name and Alias in a two-column grid. Alias includes helper text while Display name does not. The outer grid stretches both field containers to the same height, allowing the shorter field's internal grid rows to stretch and misaligning its label and input.

## Design

Add `items-start` to the existing responsive two-column field grid in `connection-dialog.tsx`. This keeps each field container at its intrinsic height and top-aligns the labels and inputs without adding placeholder content or restructuring the form.

The mobile single-column layout, Alias helper text, field behavior, and all other dialog spacing remain unchanged.

## Verification

Add a focused source-layout regression test that verifies the core fields grid opts into start alignment. Run the focused test, web lint, formatting check for the changed files, and web build. If the local app can be run without mutating external state, visually verify the dialog at desktop and narrow widths.

Do not stage, commit, push, or create a PR.
