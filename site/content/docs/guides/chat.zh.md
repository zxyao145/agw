---
title: "Chat 与执行记录"
description: "运行对话、发送图片、处理人工输入并检查执行状态。"
weight: 40
lastmod: 2026-09-15
translationKey: docs/guides/chat
---

Chat 是向 Agent 或 Agentflow 提交任务、查看结果的地方。同一会话可以连续讨论一个问题，并保留回复和工具活动；不同主题可以新建会话，方便以后查找。

开始前，确认已连接预期的 Server，并有一个可运行的 Agent 或 Agentflow。第一次使用可先完成[简单对话]({{< relref "/docs/start/first-chat" >}})。

## 一次交互

1. 打开 Chat，选择 Project 和执行目标。
2. 输入任务，按需附加图片，发送消息。
3. 查看流式回复与工具活动；如果出现审批或用户输入请求，在当前会话中处理。
4. 回看会话记录，确认结果与执行状态。

Web、Desktop、Mobile 支持文本和 JPEG、PNG、GIF、WebP 图片。每条消息最多 5 张，单张最多 5 MB，总计最多 10 MB。模型是否理解图片还取决于所选模型能力。

![AGW Desktop 对话界面：顶部选择 Project 和 Agent，底部输入消息。](/images/screenshots/desktop-chat.png)
{caption="AGW Desktop 对话界面：顶部选择 Project 和 Agent，底部输入消息。"}

## 怎样判断任务进行到哪一步

| 看到的情况 | 接下来做什么 |
| --- | --- |
| 回复或工具活动持续增加 | 等待执行完成，留意是否有错误 |
| 出现工具审批 | 查看操作名称、参数和路径，再决定是否同意 |
| 出现补充信息请求 | 在当前会话回答问题，让任务继续 |
| 执行结束 | 核对回复；涉及文件时检查实际文件或差异 |
| 报错或断线 | 先检查执行状态，再决定是否重试，避免重复操作 |

“执行成功”表示处理过程成功结束，结果是否满足要求仍需检查。例如，要求修改文档时，应查看实际改动，而不只看 Agent 的完成说明。

## 状态与连接

关闭页面或断开连接不应被理解为取消执行。需要停止任务时使用界面提供的中断操作。重新连接后客户端恢复执行状态；检查任务是否仍在运行、等待输入或已经结束。

Desktop 的每个 Server、Project、Conversation 组合有独立执行连接。切换 Project 页签会改变当前显示的会话，不会自动停止后台任务。

## 历史

会话与工具活动由服务端持久化，历史采用批量写入。Host 模板将刷新间隔设为 10 秒，省略配置时的代码回退为 5 秒；不要把尚未刷新的实时输出当成已完成持久化。

如界面与预期不符，先确认 Server 和会话选择，检查等待输入状态，再看服务日志。工作目录与权限调整会在下一回合生效。

## 实现与参考

- [Conversation persistence](https://github.com/zxyao145/agw/blob/main/docs/operations/conversation-persistence.md)
- [Execution connections](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
