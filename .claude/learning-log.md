# Project Learning Log

## 2026-08-25 | Bug Root Cause | Initial Chat Measurement Can Mimic an Upward Scroll

The first virtualized chat rows can shrink after their estimated heights are measured, clamping
`scrollTop` upward while the viewport is still at the bottom. Treating every decrease in
`scrollTop` as user intent disables follow-bottom during the first streamed conversation.
**Files:** `src/clients/packages/chat-core/src/auto-scroll.ts`,
`src/clients/packages/chat-core/src/auto-scroll.test.ts`
**Resolution:** Preserve auto-scroll whenever the current metrics are within the bottom tolerance;
only interpret upward movement outside that tolerance as a pause request.
