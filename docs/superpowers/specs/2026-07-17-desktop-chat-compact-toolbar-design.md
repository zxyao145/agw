# Desktop Chat Compact Toolbar Design

## Goal

Reduce the visual height of the first toolbar row inside Desktop Chat by one control size while leaving Web Chat unchanged.

## Scope

- Render the Desktop Agent Select with the existing small button size (`h-8` instead of `h-9`).
- Render the Desktop Chat/Files tab group at `h-8`, with proportionally smaller padding and text.
- Keep the existing sidebar visibility button unchanged because it already uses the small size.
- Preserve all current behavior, ordering, accessibility labels, and responsive wrapping.
- Do not change the Web `/chat` toolbar.

## Component Design

`ChatWorkspace` receives an explicit compact-toolbar option from the Desktop `/desktop/chat` route. The Web `/chat` route keeps the default control density.

`AgentSelector` forwards a small size option to `SearchableSelect`, which forwards it to its existing Button trigger. The shared Select remains default-sized when the option is omitted.

The Chat/Files `TabsList` and `TabsTrigger` receive compact utility classes only when the Desktop compact-toolbar option is active. The shared Tabs primitives are not changed globally.

## Testing and Verification

- Add a source contract test proving that only the Desktop route enables the compact toolbar.
- Add component contract coverage proving that the small Select size is forwarded through `AgentSelector` and `SearchableSelect`.
- Run the focused Web Chat tests, lint, formatting, and the Desktop static renderer build.
- Reload Electron and visually verify the Agent Select and Chat/Files tabs are both one size smaller while the Web route remains unchanged by construction.
