
## 1. All non-WebSocket HTTP JSON API endpoints must use `Bens.Results`.

- All non-WebSocket JSON API endpoints on the backend must use the Bens.Results format to wrap and return results.
- WebSocket Handler, OAuth redirection callback, A2A protocol endpoint, and static file endpoint can continue to use their respective protocol specific response formats.

### What to do

- Return `ApiResult.Ok()`, `ApiResult.Ok(data)`, `ApiResult.BadRequest(...)`, or another appropriate `ApiResult.*` helper directly.
- Use `ErrorCode.ToApiResult()` or `AgwException.ToApiResult()` when the response must preserve a shared error code and HTTP status.
- Use `[ProducesApiResult]` for OpenAPI response metadata where applicable; it does not replace direct `ApiResult` returns.
- Let `AgwApiExceptionMiddleware` handle `AgwException` mapping automatically.

### What NOT to do

- Do not return raw `Ok(...)`, `BadRequest(...)`, `NotFound(...)`, `NoContent()`, or other bare `IActionResult` responses from controllers in the modules listed above.

### Special case

- WebSocket handlers, OAuth redirect callbacks, A2A protocol endpoints, and static file endpoints may keep their protocol-specific response formats.

## 2. Backend API Responses and Exceptions

- Expected backend application failures must throw `Agw.Shared.Exceptions.AgwException` with an `ErrorCodes` entry.
- Reuse existing error codes before adding new ones, and never renumber existing codes.
- New `ErrorCode.Code` values contain seven digits: the first three match the HTTP status code and the final four increment within that status group, for example `400_0001`, `404_0003`, or `500_0001`.
- Keep catalog messages stable and reusable. Use `new AgwException(ErrorCodes.SomeCode)` when the catalog message is sufficient; pass an override message when runtime context such as an ID, path, provider, or validation value is required.
- Do not introduce new explicit `throw new ArgumentException`, `InvalidOperationException`, `NotSupportedException`, `HttpRequestException`, or protocol-specific exceptions for expected backend application failures.
- Preserve boundary-specific behavior by translating `AgwException` at the boundary. For example, A2A internals throw `AgwException`, while `AgwA2AJsonRpcProcessor` maps it to A2A JSON-RPC errors.


## 3. Current User ID Access

- Read the current authenticated user's stable ID from `UserInfoUtil.UserId` / `UserInfoUtil.RequiredUserId`, or from the corresponding properties on an injected `IUserInfoService`.
- Use `RequiredUserId` when authentication is mandatory. Use nullable `UserId` only when an unauthenticated context is explicitly supported.
- Do not add `user` or `userId` parameters merely to pass the current user through Controllers, Application services, runtime composition, credential readers, or tool invokers.
- An explicit user ID is allowed when it is domain data or must cross an authentication-context boundary, such as a protected OAuth state, a persisted execution manifest, a queued background operation, or an administrator acting on a specified owner. Resolve the current user before crossing that boundary and restore `UserInfoUtil.Current` after temporarily switching execution context.