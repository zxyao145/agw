# FilesController Service Extraction Design

## Goal

Reduce `FilesController` to an HTTP adapter by moving file-system operations, Git orchestration, search rules, and operation logging into an HTTP-independent Application module.

## Current Problem

`FilesController` currently combines several responsibilities:

- HTTP routing, parameter binding, path validation, response status selection, and exception logging context;
- directory listing and Git status projection;
- file reading and recursive deletion;
- Git diff and reset orchestration;
- recursive and non-recursive filename search, ignore rules, sorting, and limiting.

This makes the Controller implementation difficult to understand and forces operation tests to cross the MVC interface.

## Architecture

Introduce a concrete `FileAppService` module under `Agw.Files.Application.Files`. Its interface consists of six operations: list, read, diff, delete, reset, and search.

`FilesController` remains the HTTP adapter. It owns:

- route and OpenAPI attributes;
- query parameter binding;
- `IFilePathRequestValidator` calls;
- writing the resolved path to `HttpContext.Items` for exception logging;
- mapping application outcomes and output models to the existing HTTP status codes and `Api.Dtos` response shapes.

`FileAppService` owns:

- file and directory existence checks;
- file reads and recursive deletion;
- Git status, diff, and reset orchestration through `IGitCommandService`;
- directory entry projection, deleted-file projection, sorting, and filtering;
- recursive and non-recursive filename search, ignore rules, sorting, and limits;
- operation-level logging currently performed by the Controller.

The module is registered as its concrete type. There is only one adapter, so no `IFileAppService` seam is introduced.

## Application Results

Application code must not depend on `IActionResult`, MVC status types, anonymous HTTP payloads, or `Agw.Files.Api.Dtos`.

Operations return `FileOperationResult<T>`, which contains:

- `Status`: `Success`, `NotFound`, `InvalidRequest`, or `Failure`;
- `Value`: the successful application output when applicable;
- `Message`: the stable user-facing message used by the HTTP adapter;
- `Details`: optional diagnostic details already exposed by the existing endpoints.

Application output models represent:

- listed file entries;
- filename search matches;
- Git diff output, including unchanged content;
- delete and reset mutation outcomes.

The Controller maps these models to the existing `FileListResponse`, `FileSearchResponse`, and anonymous diff/delete/reset payloads. Existing route paths, HTTP status codes, response property names, and messages remain unchanged.

## Data Flow

1. ASP.NET Core binds the request to a `FilesController` action.
2. The Controller validates and normalizes the requested path through `IFilePathRequestValidator`.
3. Invalid paths return the existing `400` response without invoking `FileAppService`.
4. Valid paths are stored in `HttpContext.Items` for `FileEndpointExceptionMappingMiddleware` logging.
5. The Controller invokes the corresponding `FileAppService` operation with the normalized path.
6. `FileAppService` performs file I/O or Git orchestration and returns an HTTP-independent outcome.
7. The Controller maps the outcome to the existing HTTP response.

## Error Handling

- Missing files or directories return `NotFound` outcomes.
- Git failures caused by the request return `InvalidRequest` outcomes.
- Git reset execution failures return `Failure` outcomes with diagnostic details.
- Unexpected I/O and authorization exceptions continue to propagate to `FileEndpointExceptionMappingMiddleware`.
- Path validation remains in the Controller so the middleware logging context and existing `400` payload remain unchanged.

## Compatibility Constraints

- Preserve all `/api/files/*` routes.
- Preserve status codes, response property names, messages, sorting, and default parameter values.
- Preserve dot-directory, `node_modules`, and `tmpclaude*` search ignore behavior.
- Preserve current Git diff filtering and deleted-file behavior, including existing path-prefix semantics.
- Do not introduce an `IFileAppService` interface.
- Do not change `IAgwFileSystem` or project-scoped storage resolution.

## Testing

- Add `FileAppServiceTests` that exercise real temporary files and a small Git adapter fake.
- Cover normal, Git-filtered, and recursive Git directory listing; deleted entries; ordering; reads; file and directory deletion; diff outcomes; reset outcomes; recursive and non-recursive search; ignore rules; and limits.
- Keep Controller tests focused on rejected paths and mapping representative application outcomes to HTTP responses.
- Add a DI test ensuring `AddFiles` resolves `FileAppService`.
- Run `Agw.Files.Tests`, then the full `Agw.slnx` test suite.

## Non-Goals

- Fixing existing file-list path-prefix behavior.
- Replacing direct `System.IO` calls with a new adapter.
- Changing public file-system SDK contracts.
- Changing HTTP response envelopes or frontend clients.
