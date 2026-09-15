---
title: "扩展 Tools 与 Integrations"
description: "选择能力归属，使用编译期工具声明与用户连接。"
weight: 40
lastmod: 2026-09-15
translationKey: docs/development/extensions
---

扩展前先确定要增加什么：一个具体操作可以写成 Tool，一组任务说明和专属工具可以通过 Skill 提供，需要用户连接外部账号的能力则适合 Integration。

先阅读[模块边界]({{< relref "/docs/development/architecture" >}})，确认能力由哪个模块负责。本页说明放置代码、注册能力和验证调用的顺序；具体声明写法可参考文末的工具示例。

## 工具扩展路径

1. 通用工具放在 `Agw.Tools`；业务工具放在所属模块的 `Application/Tools`，DTO 放 `Contracts/Tools`。
2. 引用 `Agw.Tools.Abstractions`，需要 Attribute 声明时将 `Agw.Tools.Generators` 作为 Analyzer 引用。
3. 显式声明权限、参数说明和返回类型。独立工具及使用 Attribute 声明的工具容器不能保存会话状态；状态放在 Provider、会话对象或所属存储中。
4. 在所属模块注册所需服务与生成声明。选择通过 Skill 提供，或显式加入全局目录。
5. 验证工具发现、参数、权限、项目绑定和错误映射。

生成器输出元数据、JSON Schema 和直接调用委托。不要加入运行时反射扫描兜底。Skill 工具通过 `IAgentSkillRegistration.Tools` 提供，执行时绑定 Project，不因注册生成模块就自动进入全局目录。

## 集成扩展路径

`IPluginCatalog` 拥有 Plugin、Connector、认证和能力源定义。定义是代码/内容资产；用户设置是 `PluginInstallation`，可选账号或端点是 `Connection`。不要将它们合成一张全局配置表。

先增加目录定义和必要的工具来源，再验证用户完成设置、账号进入 Ready 状态、绑定 Agent 和实际调用的全过程。凭据由 Infrastructure 加密保存并在调用时读取；读取和执行都要检查账号归属。通过 HTTP/SSE 发送凭据时使用 HTTPS。

## 选择全局工具还是 Skill 专属工具

如果工具是可单独选用的通用操作，可显式加入全局目录。如果它只服务于某个 Skill，就通过该 Skill 注册，使说明和工具一起提供给 Agent。例如，`agw-job` 的任务管理工具属于 Jobs 模块，并随 Skill 提供。

工具成功编译后，还要确认 Agent 实际能发现它。若目录中没有出现，应先检查生成声明和注册位置；若调用时失败，再检查 Project 绑定、权限及参数。编译通过并不等于已经完成运行时接入。

## 验证

至少覆盖成功调用、非法参数、权限不足、外来 Connection 和未就绪 Connection。保持编译期诊断有效，使用 fake 服务测试，不依赖真实账号或外部 CLI。

## 实现与参考

- [Tool abstractions and examples](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Tools.Abstractions/README.md)
- [Integrations](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/README.md)
