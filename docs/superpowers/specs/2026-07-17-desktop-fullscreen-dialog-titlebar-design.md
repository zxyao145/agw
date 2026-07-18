# Desktop Fullscreen Dialog Titlebar Safety Design

## Goal

Prevent native Desktop window controls from overlapping the header of any fullscreen Dialog while preserving the existing fullscreen workspace area and leaving Web Dialogs unchanged.

## Current Behavior

The Desktop shell reserves horizontal space for native window controls in its own titlebar. Radix Dialog content is rendered through a Portal at the document root, outside that shell. A fullscreen Dialog therefore covers the native titlebar area without inheriting its platform-specific spacing.

The Agentflow editor currently constructs its fullscreen layout with one-off utility classes, so the shared Dialog primitive cannot identify or protect it as a fullscreen surface.

## Shared Fullscreen Contract

Add a distinct `fullscreen` size to `DialogContent`. It owns the full-viewport positioning currently written directly by the Agentflow editor and exposes `data-size="fullscreen"` through the existing Dialog size marker.

Fullscreen Dialogs use the standard `DialogHeader`. In Desktop, that header reserves horizontal space for the native controls:

- macOS reserves `76px` on the left for the traffic lights.
- Windows and Linux reserve `146px` on the right for the titlebar overlay controls.
- Web applies no additional inset.

Only the header receives this inset. The Dialog body remains full width and height, and the whole Dialog is not shifted downward.

## Platform Exposure

`DesktopRuntimeProvider` writes whether the renderer is running in Desktop and its platform to data attributes on the document root. It updates those attributes when runtime state becomes available and removes them when the provider unmounts.

Global component CSS combines those root attributes with `data-size="fullscreen"` and the standard `data-slot="dialog-header"` marker. This keeps the shared Dialog primitive independent of Desktop runtime hooks while applying one rule to every fullscreen Dialog.

## Agentflow Migration

The Agentflow editor changes from one-off full-viewport classes to `size="fullscreen"`. Its layout, Update action, close action, canvas, and Web behavior remain otherwise unchanged.

## Alternatives Considered

- Platform checks in each fullscreen Dialog: fewer shared changes initially, but every future fullscreen Dialog could repeat or omit the safety rule.
- Move every Desktop fullscreen Dialog down by the titlebar height: protects all controls but wastes vertical workspace and unnecessarily shifts the entire editor.

The selected shared fullscreen contract centralizes the behavior and preserves the available canvas area.

## Testing and Verification

- Add a shared Dialog regression test for the `fullscreen` size contract.
- Add a Desktop runtime regression test for document-root Desktop and platform markers.
- Add an Agentflow Dialog regression test proving it opts into the shared fullscreen size and no longer owns the one-off viewport classes.
- Run each test before implementation and confirm it fails for the missing contract.
- Run the relevant Web tests, lint, and production build after implementation.
- Build the Desktop renderer and visually verify the header safe area on the available Desktop platform.

## Out of Scope

- Moving or restyling native window controls.
- Changing the spacing of non-fullscreen Dialogs.
- Changing Agentflow editor content, actions, or canvas layout.
