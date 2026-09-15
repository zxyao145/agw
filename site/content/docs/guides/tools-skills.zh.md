---
title: "Tools 与 Skills"
description: "为 Agent 选择工具和任务说明，理解权限与项目绑定。"
weight: 80
lastmod: 2026-09-15
translationKey: docs/guides/tools-skills
---

工具（Tool）负责具体操作，例如读取文件；Skill 提供完成某类任务的说明、资源和可选工具。为 Agent 选择能力时，先确定任务需要读什么、改什么，再配置相应工具和说明。

以下步骤面向已有自定义 Agent 和 Project 的用户。外部 Agent 的能力需要按其自身支持的方式配置。

## 添加能力

1. 在工具目录检查可用工具的用途和权限级别。
2. 在 Skills 管理可用 Skill，阅读其说明与前提。
3. 在 Agent 中绑定本次任务需要的工具和 Skills。
4. 在正确的 Project 中开始新回合，先验证读取类任务，再验证确实需要的写操作。

每个工具显式声明 `AgwToolPermission`。运行权限由执行管线检查；提示词写“允许”不会跳过权限或资源归属验证。

![AGW Desktop：在 Agent 的 Tools 中按组选择 ToolBlocks，并查看各组包含的工具。](/images/screenshots/tool-blocks.png)
{caption="AGW Desktop：在 Agent 的 Tools 中按组选择 ToolBlocks，并查看各组包含的工具。"}

## Local 与 Remote Skill

创建 Skill 时，可以按内容的维护方式选择模式：

| 模式 | 内容来源 | 更新方式 |
| --- | --- | --- |
| **Local（本地）** | 上传包含 `SKILL.md` 的 ZIP 包，文件保存在 AGW 服务端 | 编辑 Skill 并上传新版 ZIP 包 |
| **Remote（远程）** | 填写可通过 HTTP 或 HTTPS 下载 Skill ZIP 包的网址 | 在远程更新内容，AGW 按缓存规则重新获取；编辑并保存 Remote Skill 也会重新获取内容 |

Local 中的“本地”指 AGW 服务端，不是浏览器所在的电脑。Remote 表示说明内容来自远程地址，任务仍由 Agent 执行。

1. 在 Skills 中创建 Skill，选择 Local 或 Remote。
2. Local 填写名称和描述，上传含 `SKILL.md` 的 ZIP 包；Remote 填写 ZIP 下载地址，无需上传文件。
3. Remote 包中必须恰好有一个 `SKILL.md`，其中的 YAML 元信息需包含 `name`、`description`，正文需包含使用说明。名称和描述由远程文件提供。
4. 保存成功后，在 Agent 或 Project 中选择该 Skill，再用一个相关的小任务验证说明是否可用。

Remote Skill 当前读取包内的说明，不会下载并运行包中的脚本，也不会提供包内其他资源文件。需要这些文件时应选择 Local 模式，并准备好执行环境。

## Remote Skill 缓存

AGW 在创建或保存 Remote Skill 时获取内容，并将其缓存到数据库中，缓存有效期为 **1 小时**。

- 有效期内读取同一个 Skill 时，直接使用缓存，减少重复下载。
- 缓存过期后，在下一次需要读取该 Skill 时重新获取；不是每小时定时下载。
- 远程内容更新后，可以等待缓存过期，或编辑并保存该 Remote Skill 来重新获取内容。
- 刷新失败时会报错，不会延长旧缓存的有效期或继续使用过期内容。先检查服务端能否访问下载地址，以及 ZIP 和 `SKILL.md` 格式是否正确。

自动刷新时，远程 `name` 必须与已保存的 Skill 名称一致。若远程改了名称，需要编辑并保存 Skill 以更新定义。缓存刷新也不会改写已经加载到当前对话中的内容，验证新版说明时应让 Agent 重新读取 Skill。

## Skill 专属工具

Skill 拥有的工具只通过该 Skill 注册，在运行时绑定 Project，不是全局工具目录条目。没有在全局列表看到它，不代表它不可用。

例如 `agw-job` 提供 `agw_job_list`、`agw_job_get`、`agw_job_create`、`agw_job_update`、`agw_job_delete`。读取在 Plan 模式可用，写入在 Plan 模式禁止。

![AGW Desktop：在 Agent 的 Skills 中搜索并选择 agw-job。](/images/screenshots/skill-selection.png)
{caption="AGW Desktop：在 Agent 的 Skills 中搜索并选择 agw-job。"}

## 检查结果

查看工具活动的名称、参数及返回值，确认使用了预期的 Project。能力缺失时检查 Skill 绑定和运行模式；实际需要外部服务时，继续配置 [MCP]({{< relref "/docs/guides/mcp" >}}) 或 [Integrations]({{< relref "/docs/guides/integrations" >}})。

## 实现与参考

- [Tools](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Tools/README.md)
- [Skill-owned Job tools](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Jobs/README.zh-CN.md)

- [Skill management](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Skills/Application/SkillAppService.cs)
- [Remote Skill cache](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Skills/Application/Remote/RemoteSkillContentResolver.cs)
