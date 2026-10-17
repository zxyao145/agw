// sample 的公共函数：存储目录中的文件读写，以及执行会话关闭过程的记录。
// Shared helpers of the sample fixture: storage directory files and execution session close records.

/** 存储目录中的文件 URI。A file URI inside the storage directory. */
export function storageFile(api, name) {
  return `${api.context.get().extension.storageUri}/${name}`;
}

export async function writeJson(api, name, value) {
  await api.fs.writeFile(storageFile(api, name), `${JSON.stringify(value, null, 2)}\n`);
}

let appendQueue = Promise.resolve();

/** 向存储目录中的 JSON 数组追加一项，多次调用按顺序执行。Appends an entry to a JSON array in the storage directory; calls run in order. */
export function appendJson(api, name, entry) {
  const append = appendQueue.then(async () => {
    const uri = storageFile(api, name);
    const entries = (await api.fs.exists(uri)) ? JSON.parse(await api.fs.readTextFile(uri)) : [];
    entries.push(entry);
    await api.fs.writeFile(uri, `${JSON.stringify(entries, null, 2)}\n`);
  });
  // 失败由调用方的 Promise 报告，队列继续处理后续追加。The caller's promise reports a failure; the queue keeps going.
  appendQueue = append.then(
    () => undefined,
    () => undefined,
  );
  return append;
}

/** 执行一次操作并记录结果：成功为 "ok"，失败为错误的 code。Runs an operation and records "ok" or the error code. */
async function outcomeOf(operation, run) {
  try {
    await run();
    return { operation, outcome: "ok" };
  } catch (error) {
    return { operation, outcome: error.code ?? null, message: error.message };
  }
}

/**
 * 打开并跟踪执行会话；frame 关闭钩子调用 `writeReport` 把记录写入存储目录。
 * Opens tracked execution sessions; the frame's closing hook calls `writeReport` to save the records to storage.
 */
export function createSessionTracker(api) {
  const opens = [];
  const closes = [];
  const duringClose = [];
  const pending = [];
  let lastProjectId = null;

  // 关闭期间尝试新建会话与文件监听。Tries to create a session and a file watch while closing.
  const tryCreate = () =>
    Promise.all([
      outcomeOf("createSession", () =>
        api.execution.createSession({ projectId: lastProjectId ?? api.context.get().project?.id }),
      ),
      outcomeOf("watch", () => api.fs.watch(api.context.get().extension.storageUri, {}, () => undefined)),
    ]);

  async function open(projectId) {
    lastProjectId = projectId;
    const opening = api.execution.createSession({ projectId });
    pending.push(
      opening.then(
        () => undefined,
        (error) => {
          opens.push({ at: Date.now(), outcome: error.code ?? null, message: error.message });
        },
      ),
    );
    const session = await opening;
    opens.push({ at: Date.now(), outcome: "ok", conversationId: session.conversationId });
    session.onClose((reason) => {
      closes.push({ reason, at: Date.now() });
      pending.push(tryCreate().then((outcomes) => duringClose.push(...outcomes)));
    });
    return session.conversationId;
  }

  async function writeReport(name, hookAt) {
    const duringHook = await tryCreate();
    await Promise.all(pending);
    await writeJson(api, name, { hookAt, closes, duringClose, duringHook, opens });
  }

  return { open, writeReport };
}
