# Agentflow Connection Handle Size Design

## Goal

Make Agentflow node connection points easier to see and drag in both Web and Desktop without changing node layout or connection behavior.

## Current Behavior

`VisualAgentflowBuilder` renders both target and source React Flow handles at `12×12px` with a `2px` background-colored border. Desktop packages the same Web renderer, so the small shared handles appear in both clients.

## Design

- Increase both target and source handles to `20×20px`.
- Increase the contrasting border to `3px` so the larger handle remains visually distinct over the node border and canvas.
- Apply the geometry, border, and stacking values through React's `style` prop because React Flow's stylesheet loads after Tailwind utilities and otherwise restores its `6×6px`, `1px` defaults.
- Preserve the current sky target color and emerald source color.
- Preserve React Flow positioning so each handle remains centered on its existing left or right node edge anchor.
- Render the handles as siblings of the clipped `Card`, inside a shared relative node wrapper, so the full circles can extend beyond the node border.
- Keep the handles above the node surface with an explicit stacking level.
- Keep `overflow-hidden` on the `Card` so its header background still follows the node's rounded corners.
- Use the larger handle itself as the larger pointer target; do not add a separate overlay or change connection logic.

The change belongs only in the shared `DagNode` implementation. Desktop receives it through its packaged Web renderer and does not need a separate style override.

## Alternatives Considered

- `16×16px`: less visually prominent, but provides a smaller usability improvement.
- Transparent expanded hit area: improves dragging without addressing the reported visibility problem.
- Removing `overflow-hidden` from the `Card`: exposes the full handles, but also removes the existing rounded-corner clipping contract from node content.
- Moving the handles farther inside the node: avoids clipping, but weakens the visual connection between each handle and its edge anchor.

The selected `20×20px` handle directly improves both visibility and interaction while keeping the implementation minimal.

## Testing and Verification

- Add a focused source regression test that requires both target and source handles to share explicit `20×20px`, `3px`, and `z-index: 10` runtime styles.
- Require the regression test to verify that both handles live outside the clipped `Card` and use a stacking level above its border.
- Run the test before implementation to confirm it fails for the current `12×12px` handles.
- Apply the minimal shared component change and rerun the focused test.
- Run the relevant Agentflow tests, Web lint, and Web production build.
- Verify the Desktop renderer build consumes the same updated Agentflow component.

## Out of Scope

- Changing node dimensions, colors, edge styles, anchor positions, or connection validation.
- Changing connection points outside the Agentflow editor.
- Adding Desktop-only Agentflow styling.
