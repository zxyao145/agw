import assert from "node:assert/strict";
import test from "node:test";

import type { ConfirmationView } from "../../shared/contracts";
import { ConfirmationService } from "./confirmation-service";

type TestWindow = {
  view: ConfirmationView;
  contents: object;
  closed: boolean;
  shownHeight: number | null;
  onClosed(): void;
};

/** 确认服务与它打开的窗口；窗口由测试代替用户操作。The service and the windows it opens; the test acts for the user. */
function createService() {
  const windows: TestWindow[] = [];
  const service = new ConfirmationService({
    openWindow: (view, onClosed) => {
      const window: TestWindow = { view, contents: {}, closed: false, shownHeight: null, onClosed };
      windows.push(window);
      return {
        contents: window.contents,
        show: (height) => (window.shownHeight = height),
        close: () => (window.closed = true),
      };
    },
  });
  return { service, windows };
}

const LINK: ConfirmationView = {
  kind: "open-link",
  extensionId: "sample",
  extensionName: "Sample",
  url: "https://example.com/",
};
const MESSAGE: ConfirmationView = {
  kind: "message",
  extensionId: "sample",
  extensionName: "Sample",
  type: null,
  message: "Save?",
  detail: null,
  buttons: ["Save", "Cancel"],
  defaultId: 0,
  cancelId: 1,
};
const ENABLE: ConfirmationView = {
  kind: "enable",
  extensionId: "sample",
  extensionName: "Sample",
  version: "1.0.0",
  installedVersion: null,
  downgrade: false,
  permissions: [],
  hostPermissions: [],
  sendsData: false,
  runsAgents: false,
};

test("requests show one at a time in order and only their window can read and answer them", async () => {
  const { service, windows } = createService();
  const first = service.request(LINK);
  const second = service.request(MESSAGE);
  assert.equal(windows.length, 1);
  const { requestId, view } = service.requestFor(windows[0].contents);
  assert.deepEqual(view, LINK);

  assert.throws(() => service.requestFor({}), /no longer valid/u);
  await assert.rejects(service.answer({}, requestId, true), /no longer valid/u);
  await assert.rejects(service.answer(windows[0].contents, "other-id", true), /no longer valid/u);
  await assert.rejects(service.answer(windows[0].contents, requestId, 1), /does not answer/u);

  service.ready(windows[0].contents, requestId, 240);
  assert.equal(windows[0].shownHeight, 240);
  await service.answer(windows[0].contents, requestId, true);
  assert.equal(await first, true);
  assert.equal(windows[0].closed, true);

  // 标识只能使用一次。The id is single-use.
  await assert.rejects(service.answer(windows[0].contents, requestId, true), /no longer valid/u);

  assert.equal(windows.length, 2);
  const message = service.requestFor(windows[1].contents);
  await assert.rejects(
    service.answer(windows[1].contents, message.requestId, true),
    /does not answer/u,
  );
  await assert.rejects(
    service.answer(windows[1].contents, message.requestId, 2),
    /does not answer/u,
  );
  await service.answer(windows[1].contents, message.requestId, 0);
  assert.equal(await second, 0);
});

test("closing the window or withdrawing the request cancels it", async () => {
  const { service, windows } = createService();
  const owner = {};
  const closed = service.request(LINK);
  const queued = service.request(MESSAGE, { owner, clientId: "box-1" });
  const shownLater = service.request(MESSAGE, { owner, clientId: "box-2" });

  windows[0].onClosed();
  assert.equal(await closed, null);

  // 其他发起方不能撤回。Another requester cannot withdraw it.
  service.dismiss({}, "box-1");
  assert.equal(windows.length, 2);
  service.dismiss(owner, "box-1");
  assert.equal(await queued, null);
  assert.equal(windows[1].closed, true);

  assert.equal(windows.length, 3);
  service.dismiss(owner, "box-2");
  assert.equal(await shownLater, null);
});

test("an approval re-checks its bound data and a failed check counts as canceled", async () => {
  const { service, windows } = createService();
  let current = true;
  const checks: string[] = [];
  const verify = async () => {
    checks.push("verify");
    return current;
  };

  const declined = service.request(ENABLE, { verify });
  await service.answer(
    windows[0].contents,
    service.requestFor(windows[0].contents).requestId,
    false,
  );
  assert.equal(await declined, false);
  assert.deepEqual(checks, []);

  const approved = service.request(ENABLE, { verify });
  await service.answer(
    windows[1].contents,
    service.requestFor(windows[1].contents).requestId,
    true,
  );
  assert.equal(await approved, true);
  assert.deepEqual(checks, ["verify"]);

  current = false;
  const changed = service.request(ENABLE, { verify });
  const { requestId } = service.requestFor(windows[2].contents);
  const answering = service.answer(windows[2].contents, requestId, true);
  // 复核期间的重复回答不产生第二个结果。A repeated answer during the check produces no second result.
  await service.answer(windows[2].contents, requestId, true);
  await answering;
  assert.equal(await changed, null);
  assert.deepEqual(checks, ["verify", "verify"]);
});

test("ready rejects an invalid height", () => {
  const { service, windows } = createService();
  void service.request(LINK);
  const { requestId } = service.requestFor(windows[0].contents);
  assert.throws(() => service.ready(windows[0].contents, requestId, Number.NaN), /height/u);
});
