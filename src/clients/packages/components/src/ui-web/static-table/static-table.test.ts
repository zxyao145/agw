import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

const { React, render, screen } = await setupDomEnvironment();
const { StaticTable } = await import("./index.tsx");
const { TableBody, TableCell, TableHead, TableHeader, TableRow } = await import("../shadcn/table");
const { Empty, EmptyContent, EmptyHeader, EmptyTitle } = await import("../shadcn/empty");

function header() {
  return React.createElement(
    TableHeader,
    { key: "header", className: "existing-header" },
    React.createElement(
      TableRow,
      null,
      React.createElement(TableHead, null, "Name"),
      React.createElement(TableHead, null, "Updated"),
    ),
  );
}

function body() {
  return React.createElement(
    TableBody,
    { key: "body" },
    React.createElement(
      TableRow,
      null,
      React.createElement(TableCell, null, "Reviewer"),
      React.createElement(TableCell, null, "2026-09-21"),
    ),
  );
}

function empty() {
  return React.createElement(
    Empty,
    { key: "empty" },
    React.createElement(EmptyHeader, null, React.createElement(EmptyTitle, null, "No agents yet")),
    React.createElement(EmptyContent, null, "Create one to get started."),
  );
}

test("a populated table renders its header and rows", () => {
  render(React.createElement(StaticTable, { isEmpty: false }, header(), body()));

  assert.ok(screen.getByRole("columnheader", { name: "Name" }));
  assert.ok(screen.getByRole("cell", { name: "Reviewer" }));
  assert.equal(screen.getAllByRole("row").length, 2);
});

test("the header keeps the caller's classes next to the shared muted background", () => {
  const view = render(React.createElement(StaticTable, { isEmpty: false }, header(), body()));
  const renderedHeader = view.container.querySelector("thead");

  assert.ok(renderedHeader);
  assert.ok(renderedHeader.className.includes("existing-header"));
  assert.ok(renderedHeader.className.includes("bg-muted/30"));
});

test("an empty table renders the caller's empty state instead of the table", () => {
  render(React.createElement(StaticTable, { isEmpty: true }, header(), body(), empty()));

  assert.ok(screen.getByText("No agents yet"));
  assert.equal(screen.queryByRole("table"), null);
  assert.equal(screen.queryByRole("cell", { name: "Reviewer" }), null);
});

test("an empty table without an empty state falls back to a plain message", () => {
  render(React.createElement(StaticTable, { isEmpty: true }, header(), body()));

  assert.ok(screen.getByText("No data found."));
});

test("extra children render after the table body", () => {
  render(
    React.createElement(
      StaticTable,
      { isEmpty: false },
      header(),
      body(),
      React.createElement("caption", { key: "caption" }, "Managed agents"),
    ),
  );

  assert.ok(screen.getByText("Managed agents"));
});

test("an embedded table drops the standalone border so a parent surface owns it", () => {
  const standalone = render(React.createElement(StaticTable, { isEmpty: false }, header(), body()));
  assert.equal(
    standalone.container.firstElementChild?.className,
    "overflow-hidden rounded-md border",
  );
  standalone.unmount();

  const embedded = render(
    React.createElement(StaticTable, { isEmpty: false, embedded: true }, header(), body()),
  );
  assert.equal(embedded.container.firstElementChild?.className, "overflow-hidden");
});
