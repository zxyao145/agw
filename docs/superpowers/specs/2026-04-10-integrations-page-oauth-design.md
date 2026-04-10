# Integrations Page OAuth Management Design

## Summary

Replace the current static integrations launchpad with a real management page backed by `AppDefinition` and `AppInstance`.
The page will show existing app connections in the top section, available app definitions in a responsive grid below, and use a modal to create a new `AppInstance` before starting OAuth2.

## Current Context

- `src/frontend/web/src/app/(app)/integrations/page.tsx` is currently a static prototype with hard-coded templates and a purely frontend-built OAuth URL.
- The backend now exposes:
  - `GET /api/integrations/app-definitions`
  - `GET /api/integrations/app-instances`
  - `DELETE /api/integrations/app-instances/{id}`
- The backend does not yet expose a way to:
  - create a new `AppInstance`
  - start OAuth authorization for a specific `AppInstance`
  - reconnect an existing `AppInstance`
- `src/backend/Agw.Integrations/Controllers/OauthController.cs` currently exchanges tokens using configuration keyed by provider and assumes a single configured `AppInstanceId` per provider.
- `src/frontend/web/src/app/(app)/integrations/callback/page.tsx` already renders the OAuth callback result and can remain the landing page after token exchange.

## Requirements

### User-Facing Requirements

1. The integrations page must be split into two major sections:
   - top: all `AppInstance` entries
   - bottom: all `AppDefinition` entries
2. The `AppDefinition` section must use a responsive grid that automatically adapts the number of columns based on available width.
3. Clicking an `AppDefinition` card must open a modal.
4. Inside the modal:
   - `AppDefinition` fields are shown as read-only
   - `ClientId`, `ClientSecret`, and `UsePkce` are editable
5. Submitting the modal must:
   - create a persistent `AppInstance`
   - immediately start the OAuth2 authorization flow for that instance
6. Each `AppInstance` card must expose:
   - `Reconnect`
   - `Delete`
7. `Reconnect` must re-run OAuth2 for the existing instance without creating a second record.
8. The page must reflect authorization state using backend data:
   - authorized or not
   - authorization expired or not
   - authorization subject when present

### Security and Data Boundaries

- The page must never render `ClientSecret` after the instance has been created.
- OAuth URL assembly must move to the backend once instance-based authorization is introduced.
- The callback flow must resolve the target `AppInstance` from backend-controlled state instead of a static configured `AppInstanceId`.

## Proposed UX

## Page Layout

Use a vertical two-section layout inside the existing page container.

### Top Section: App Instances

- Section title: `Connected apps`
- Show cards in a 1-column mobile layout and a 2-column desktop layout.
- Each card shows:
  - app display name
  - provider name
  - `ClientId`
  - authorization status badge
  - expiration badge when expired
  - subject when present
  - created time
- Card actions:
  - primary: `Reconnect`
  - destructive secondary: `Delete`
- If no instances exist, show an empty-state card with a short explanation and a cue to create one from the catalog below.

### Bottom Section: App Definitions

- Section title: `App catalog`
- Use a CSS grid with `repeat(auto-fit, minmax(...))` behavior so the number of columns adapts automatically.
- Each card shows:
  - display name
  - provider
  - description
  - scopes
  - default PKCE hint
- Clicking a card opens the create-and-connect modal.

### Create And Connect Modal

Use `DialogContent size="lg"` with two stacked blocks.

#### Read-Only Definition Block

Show the selected `AppDefinition` metadata as non-editable content:

- display name
- provider
- description
- authorization URL
- scopes
- default PKCE behavior

This block is for review and must not use editable inputs.

#### Editable Connection Block

Show editable controls for:

- `Client ID`
- `Client Secret`
- `Use PKCE`

Rules:

- `Client ID` is required
- `Client Secret` is required for this first iteration
- `Use PKCE` defaults to the selected definition's `UsePkce`

Footer actions:

- `Cancel`
- `Connect with OAuth2`

## Backend Design

## AppDefinition Metadata

`AppDefinition` needs enough backend metadata for instance-based OAuth token exchange.
In addition to the current UI-oriented fields, the backend-owned catalog should include:

- token endpoint
- optional subject field path used during token parsing

These fields do not need to be displayed in the UI, but they are required by `authorize-start` and callback handling.

## New Contracts

### Create AppInstance

Add:

- `POST /api/integrations/app-instances`

Request body:

- `appName`
- `clientId`
- `clientSecret`
- `usePkce`

Response:

- return the created instance in the same shape as the list item response

### Start Authorization

Add:

- `POST /api/integrations/app-instances/{id}/authorize-start`

Response body:

- `authorizeUrl`

This endpoint will:

1. load the target `AppInstance`
2. load the corresponding `AppDefinition`
3. generate state
4. generate PKCE values when required
5. persist callback state in a backend-controlled cookie payload
6. build the final provider authorization URL
7. return the URL to the frontend

## Callback Changes

`/api/integrations/oauth/callback` must stop relying on a provider configuration with a fixed `AppInstanceId`.

Instead it must:

1. read the callback state cookie
2. resolve `AppInstanceId` from that state
3. load the persisted `AppInstance`
4. load OAuth metadata from the matching `AppDefinition`
5. use the instance `ClientId`, `ClientSecret`, and `UsePkce` data when exchanging the code
6. upsert the resulting token onto that specific instance

This allows both:

- newly created instances
- reconnect flows for existing instances

to reuse the same callback path safely.

## Frontend Data Flow

## Initial Load

When `/integrations` loads:

1. fetch `AppDefinition` list
2. fetch `AppInstance` list
3. render both sections

The two requests can run in parallel.

## Create And Connect Flow

When the user clicks an `AppDefinition` card:

1. open the modal with:
   - readonly definition metadata
   - editable connection fields
2. on submit:
   - call `POST /api/integrations/app-instances`
   - call `POST /api/integrations/app-instances/{id}/authorize-start`
   - redirect the browser to the returned `authorizeUrl`

## Reconnect Flow

When the user clicks `Reconnect` on an existing instance:

1. call `POST /api/integrations/app-instances/{id}/authorize-start`
2. redirect the browser to the returned `authorizeUrl`

No modal is needed for reconnect because the persisted instance already owns the connection settings.

## Delete Flow

When the user clicks `Delete`:

1. show a lightweight confirmation dialog or `confirm()` for this first iteration
2. call `DELETE /api/integrations/app-instances/{id}`
3. remove the item locally or refetch the list

## Error Handling

- Failed definition or instance loads show a page-level error card with retry.
- Failed create, reconnect, or delete actions show toast errors.
- Modal validation errors stay inline next to the corresponding field.
- If authorization start fails after instance creation, keep the newly created instance and show an error toast. The user can use `Reconnect` from the instance list afterward.

## Testing Strategy

## Backend Tests

Add controller or integration tests for:

- create app instance success
- create app instance rejects invalid payload
- authorize-start returns a provider URL for an existing instance
- authorize-start returns `404` for a missing instance
- callback uses callback state to resolve the correct instance
- reconnect updates or overwrites the token for the same instance

## Frontend Verification

Verify:

- definitions render in a responsive auto-fit grid
- instances render above definitions
- clicking a definition opens the modal
- modal shows readonly definition data and editable client credentials
- create then connect triggers backend create and authorization start
- reconnect triggers authorization start without opening the modal
- delete removes an instance from the list

## Scope Boundaries

- This change does not introduce `AppInstance` edit support.
- This change does not add provider-specific custom fields beyond `ClientId`, `ClientSecret`, and `UsePkce`.
- This change keeps the callback landing page at `/integrations/callback`.
