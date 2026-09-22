import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

const { React, act, fireEvent, render, screen, within } = await setupDomEnvironment();
const { PaginatedTable } = await import("./table-pagination.tsx");

type PaginationState = {
  pageIndex?: number;
  pageSize?: number;
  total?: number;
  isFetching?: boolean;
  onPageIndexChange?: (pageIndex: number) => void;
  onPageSizeChange?: (pageSize: number) => void;
};

function renderPaginatedTable(state: PaginationState = {}) {
  return render(
    React.createElement(PaginatedTable, {
      pageIndex: state.pageIndex ?? 1,
      pageSize: state.pageSize ?? 20,
      total: state.total ?? 45,
      isFetching: state.isFetching ?? false,
      onPageIndexChange: state.onPageIndexChange ?? (() => {}),
      onPageSizeChange: state.onPageSizeChange ?? (() => {}),
      children: React.createElement("div", { "data-testid": "table" }, "table rows"),
    }),
  );
}

test("pagination reports the visible range and page count", () => {
  renderPaginatedTable({ pageIndex: 2, pageSize: 20, total: 45 });

  assert.ok(screen.getByText("Showing 21–40 of 45"));
  assert.ok(screen.getByText("Page 2 of 3"));
});

test("the table content stays inside the paginated surface", () => {
  const view = renderPaginatedTable();
  const surface = view.container.firstElementChild;

  assert.ok(surface);
  assert.ok(within(surface as HTMLElement).getByTestId("table"));
  assert.ok(within(surface as HTMLElement).getByLabelText("Rows per page"));
});

test("page controls move one page at a time", () => {
  const requestedPages: number[] = [];
  renderPaginatedTable({ pageIndex: 2, onPageIndexChange: (page) => requestedPages.push(page) });

  fireEvent.click(screen.getByLabelText("Next page"));
  fireEvent.click(screen.getByLabelText("Previous page"));

  assert.deepEqual(requestedPages, [3, 1]);
});

test("the first page cannot go back and the last page cannot go forward", () => {
  const view = renderPaginatedTable({ pageIndex: 1, total: 45 });
  assert.equal(screen.getByLabelText("Previous page").hasAttribute("disabled"), true);
  assert.equal(screen.getByLabelText("Next page").hasAttribute("disabled"), false);
  view.unmount();

  renderPaginatedTable({ pageIndex: 3, total: 45 });
  assert.equal(screen.getByLabelText("Previous page").hasAttribute("disabled"), false);
  assert.equal(screen.getByLabelText("Next page").hasAttribute("disabled"), true);
});

test("a fetch in flight blocks both page controls", () => {
  renderPaginatedTable({ pageIndex: 2, total: 45, isFetching: true });

  assert.equal(screen.getByLabelText("Previous page").hasAttribute("disabled"), true);
  assert.equal(screen.getByLabelText("Next page").hasAttribute("disabled"), true);
});

test("choosing a page size reports the new size", async () => {
  const requestedSizes: number[] = [];
  renderPaginatedTable({ onPageSizeChange: (size) => requestedSizes.push(size) });

  await act(async () => {
    fireEvent.click(screen.getByLabelText("Rows per page"));
  });
  await act(async () => {
    fireEvent.click(await screen.findByRole("option", { name: "50" }));
  });

  assert.deepEqual(requestedSizes, [50]);
});

test("an empty result hides the pagination controls and keeps the table", () => {
  renderPaginatedTable({ total: 0 });

  assert.ok(screen.getByTestId("table"));
  assert.equal(screen.queryByLabelText("Rows per page"), null);
  assert.equal(screen.queryByLabelText("Next page"), null);
});
