---
title: "Chat 与执行记录"
description: "运行对话、发送图片、处理人工输入并检查执行状态。"
weight: 40
lastmod: 2026-09-25
translationKey: docs/guides/chat
---

Chat 是向 Agent 或 Agentflow 提交任务、查看结果的地方。同一会话可以连续讨论一个问题，并保留回复和工具活动；不同主题可以新建会话，方便以后查找。

开始前，确认已连接预期的 Server，并有一个可运行的 Agent 或 Agentflow。第一次使用可先完成[简单对话]({{< relref "/docs/start/first-chat" >}})。

## 一次交互

1. 打开 Chat，选择 Project：Desktop 在窗口顶部的项目标签页中选择，Web 在左侧栏顶部的下拉框中选择。然后在输入框左上方的选择器中选择执行目标。
2. 输入任务，按需附加图片，按 Ctrl/Shift+Enter 或点击发送按钮发送；单独按 Enter 换行。
3. 查看流式回复与工具活动；如果出现审批或用户输入请求，在当前会话中处理。
4. 回看会话记录，确认结果与执行状态。

Web、Desktop、Mobile 支持文本和 JPEG、PNG、GIF、WebP 图片。Web 和 Desktop 通过粘贴添加图片，Mobile 从相册选择。每条消息最多 5 张，单张最多 5 MB，总计最多 10 MB。模型是否理解图片还取决于所选模型能力。

![AGW Desktop 对话界面：选择 Project 和 Agent 后输入消息。](/images/screenshots/desktop-chat.png)
{caption="AGW Desktop 对话界面：选择 Project 和 Agent 后输入消息。"}

## 输入框中的辅助功能

| 操作 | 作用 |
| --- | --- |
| 在行首或空格后输入 `/` | 显示命令建议：自定义 Agent 列出可用的 Skills 和 Tools，Claude Code 列出它的斜杠命令 |
| 输入 `@` | 搜索当前 Project 中的文件，最多显示 8 条 |
| 上下方向键与 Enter | 在建议列表中切换并选中 |
| **+** 按钮 | 开启 Plan mode（配置了 Mode ToolBlock 的自定义 Agent），或插入 Skills 和 Tools |
| 闪电按钮 | 打开 **Quick Text Insert**，插入在 **Quick prompts** 页面维护的常用文字 |
| 权限下拉框 | 选择工具权限模式，修改在下一回合生效 |
| **Go to latest message** / **Go to first message** | 跳转到最新消息，或加载完整历史并跳转到第一条消息 |

**Quick prompts** 页面分为 **My prompts**（当前用户自己的条目）和 **System prompts**（所有用户可见，只有管理员可以编辑）。Web 在导航中打开这个页面，Desktop 在 Settings 中打开。

## 怎样判断任务进行到哪一步

| 看到的情况 | 接下来做什么 |
| --- | --- |
| 回复或工具活动持续增加 | 等待执行完成，留意是否有错误 |
| 出现工具审批 | 查看操作名称、参数和路径，再决定是否同意 |
| 出现补充信息请求 | 在当前会话回答问题，让任务继续 |
| 执行结束 | 核对回复；涉及文件时检查实际文件或差异 |
| 报错或断线 | 先检查执行状态，再决定是否重试，避免重复操作 |

会话列表中的图标显示每个会话的状态：**Running** 表示仍在运行，**Last turn failed** 表示上一轮失败，**Last turn interrupted** 表示上一轮被中断。

回合结束并产生 Result 后，这一回合的工具活动和中间消息会收起为一行 “Worked for …”，点击可以展开查看。模型的推理内容默认收起，点击 **Expand reasoning** 展开。鼠标移到用户消息或 Result 上时，会显示发送时间和 **Copy message** 按钮。

“执行成功”表示处理过程成功结束，结果是否满足要求仍需检查。例如，要求修改文档时，应查看实际改动，而不只看 Agent 的完成说明。

## 会话列表与会话设置

会话列表顶部可以刷新列表、删除全部历史（**Delete All History**），并通过 Info 按钮打开 **Conversation Settings**；每个会话可以重命名或删除。

Conversation Settings 显示当前会话的 ID、消息数、创建时间和更新时间，还提供两项设置：

- **Only Stream Turn Result**：只推送每一回合的 Result，并自动拒绝需要人工处理的提问和工具审批；会话历史仍完整保存。它只对 External Agent 和开启 Generate Turn Summary 的自定义 Agent 生效，从下一回合开始生效。
- **Environment Variables**：随执行发送的环境变量。

这些设置按 Project 保存在当前客户端中，换一台设备或浏览器需要重新设置。

## 状态与连接

关闭页面或断开连接通常不会取消执行。InProcess 模式下，如果断线时本轮正在等待审批、用户输入或 HumanGate，Server 会中断本轮；Distributed 模式下断线不会中断执行。需要停止任务时使用界面提供的中断操作。

连接中断时，界面显示 “Reconnecting to Server…” 并自动重试，也可以点击 **Retry now** 立即重试。重新连接后客户端恢复执行状态；检查任务是否仍在运行、等待输入或已经结束。

Desktop 的每个 Server、Project、Conversation 组合有独立执行连接。切换 Project 页签会改变当前显示的会话，不会自动停止后台任务；项目标签页上的状态点显示后台任务状态，关闭仍有任务运行的标签页前会先确认。

## 历史

会话与工具活动由服务端持久化，历史采用批量写入。Host 模板将刷新间隔设为 10 秒，省略配置时的代码回退为 5 秒；不要把尚未刷新的实时输出当成已完成持久化。

打开会话时先显示最近的消息，向上滚动时每次加载 50 条较早的消息。Desktop 还提供用户输入导航，列出会话中的每一条用户输入，包括尚未加载的早期输入；点击后跳转到该输入，并按需加载更早的历史。

回合被中断或失败时，未完成的消息和带致命错误的消息仍显示在会话中，但不会进入后续回合的模型上下文，也不会交给切换后的 Agent。

如界面与预期不符，先确认 Server 和会话选择，检查等待输入状态，再看服务日志。工作目录与权限调整会在下一回合生效。

## 实现与参考

- [Conversation persistence](https://github.com/zxyao145/agw/blob/main/docs/operations/conversation-persistence.md)
- [Execution connections](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
