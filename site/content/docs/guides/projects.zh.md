---
title: "Projects、文件与工作目录"
description: "设置主目录与附加目录，区分文件浏览目录与 Agent 默认工作目录。"
weight: 50
lastmod: 2026-09-15
translationKey: docs/guides/projects
---

Project 把一项工作的文件目录、背景资料和会话放在一起。例如，为一个代码仓库创建 Project 后，可以分别建立“阅读代码”和“排查问题”的会话，同时使用同一份工作目录。

工作目录必须存在，并且运行 AGW Server 的账号能够访问。连接远程 Server 时应填写远程主机上的路径；使用 Docker 时应填写容器内的路径。

## 配置工作空间

1. 创建 Project，填写主工作目录 `Workspace`。留空时默认使用 `~/.agw/projects/{projectId:N}`。
2. 如需同时浏览其他路径，添加 Additional directories。每个关联有稳定 ID；修改路径会生成新 ID。
3. 在文件界面的目录下拉框切换浏览根，检查文件与 Git 访问。
4. 在该 Project 中运行 Agent，验证任务使用预期的主工作目录。

切换文件浏览目录不会改变 Agent 的默认工作目录。移除附加目录关联不会删除磁盘文件。网络存储应先通过操作系统或容器挂载，再将挂载后的路径配置为工作目录。

![AGW Desktop：为 Project 设置主工作目录和附加目录。图中的示例路径需替换为执行主机上的实际目录。](/images/screenshots/project-directories.png)
{caption="AGW Desktop：为 Project 设置主工作目录和附加目录。图中的示例路径需替换为执行主机上的实际目录。"}

## 一个主目录与附加目录的例子

假设代码位于 `/work/app`，参考资料位于 `/work/reference`：将前者设为 Workspace，将后者添加为附加目录。Files 中可以切换到参考资料目录浏览，但 Agent 默认仍从 `/work/app` 开始工作。任务需要参考资料时，应说明资料位于哪个目录，并确保 Agent 具备读取能力。

Docker 中需要先把两个目录挂入容器。例如，主机的 `/home/me/app` 挂载为容器内的 `/work/app` 后，Workspace 应填写 `/work/app`。只在表单填写路径不会创建容器挂载。

留空时的 `{projectId:N}` 表示项目 ID 去掉连字符后的值，由 AGW 自动填入默认路径，不需要把这段占位文字手动填进表单。

## 修改何时生效

Project 更新会使本机文件系统缓存失效，文件浏览立即刷新。Agent 在每回合开始时捕获不可变目录快照；目录变更会在下一回合重建运行时并保留会话身份。当前回合、子执行与持久化恢复继续使用已经捕获的路径。

分布式执行的每个节点都必须能看到快照中的相同主机路径。不可用或不属于该 Project 的附加目录会失败，不会自动退回主目录。

## 排查

文件不存在时，核对当前浏览根、实际挂载、执行账号和 Server 路径。不要只在本地终端验证与 Server 不同的目录。非内置 Project 可以复制；复制后再次核对目录关联。

## 实现与参考

- [Filesystem resolver](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Files/Application/Storage/Resolver/ProjectScopedFileSystemResolver.cs)
- [Directory snapshots](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
