# TaskPilot

TaskPilot turns subscribed events into user-owned matters and serialized rounds. Rules can run an Agent or manage a GitHub pull request delivery. Round results can create questions and notices, schedule reviews, publish `taskpilot.*` events, and request receipt repair without tools.

## Build

From the `src` workspace, run:

```sh
pnpm --filter @agw/extension-taskpilot build
```

The build creates `dist/manifest.json`, the Server assembly, and the bundled Desktop view. The generated manifest includes checksums for every package file.

## Desktop

Open the TaskPilot main view from the Desktop Extensions panel. The view uses `context.project` and `context.agent` to load project and target selectors. Event source IDs are entered explicitly.

## Server API

TaskPilot endpoints are mounted under `/api/ext/taskpilot/`. The Desktop view uses rules, matters, rounds, reviews, and delivery status endpoints. Rule changes write a new revision and stage the managed subscription update in the extension outbox.

The extension publishes only event types permitted by its manifest, under the `taskpilot.*` namespace. The Server runtime derives the active user and extension generation for every invocation.
