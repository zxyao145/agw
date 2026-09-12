import { apiDelete, apiGet, apiPost, apiRequest, createBearerApiClient } from "../src/client";

// Compiled by test:types, never executed. Invalid calls must fail at the public boundary.
function checkContract() {
  apiGet("/api/agents");
  apiPost("/api/auth/login", { body: { password: "test" } });
  apiPost("/api/auth/logout");
  apiDelete("/api/auth/tokens/{id}", { params: { path: { id: "token-1" } } });
  apiGet("/api/files/list", { params: { query: { projectId: "project-1" } } });
  // @ts-expect-error A required query parameter requires options.
  apiGet("/api/files/list");
  // @ts-expect-error A required query parameter cannot be omitted.
  apiGet("/api/files/list", { params: { query: {} } });
  // @ts-expect-error Login has a required body.
  apiPost("/api/auth/login");
  // @ts-expect-error The lower-level request has the same required body.
  apiRequest("/api/auth/login", "post");
  // @ts-expect-error Login body cannot be omitted through an empty options object.
  apiPost("/api/auth/login", {});
  // @ts-expect-error Unknown login fields are rejected.
  apiPost("/api/auth/login", { body: { wrongPasswordField: "test" } });
  // @ts-expect-error The path identifier is required.
  apiDelete("/api/auth/tokens/{id}");
  // @ts-expect-error Params cannot omit the required path.
  apiDelete("/api/auth/tokens/{id}", { params: {} });
  // @ts-expect-error No GET operation is declared on login.
  apiGet("/api/auth/login");
  // @ts-expect-error The route is not in the contract.
  apiGet("/api/not-a-route");
  const bearer = createBearerApiClient({ baseUrl: "https://example.com", token: "agw_test" });
  // @ts-expect-error Isolated clients preserve required body checks.
  bearer.apiPost("/api/auth/login");
}

void checkContract;
