# Backend 低质量代码审计（第一轮）

> 审计时间：2026-04-12（UTC）
> 
> 范围：`src/backend/**`，重点检查可维护性、内聚性、可读性、无效/失活代码。

## 结论摘要

本次并未发现“创建 Task 时传入 Input 完全未使用”的情况；`Input` 在创建任务时被写入首条用户消息记录，并在任务响应中被读取回显。

但确实存在多处低质量问题，主要集中在：

1. 职责混杂、文件过大（高复杂度）；
2. 粗粒度异常与错误语义不一致；
3. 失活模块与 TODO 残留；
4. 重复逻辑和不可靠安全校验；
5. 命名/目录结构不一致，降低可读性。

---

## 详细问题清单（按优先级）


## P1（中高优先级：明显拖累可维护性）

### 3) 单类过大、职责混杂

- `AgentRuntimeService`（646 行）同时承担 agent 组装、session 生命周期、消息流处理、异常与资源释放等多个职责。
- `AgwA2ARequestHandler`（678 行）处理任务查询、取消、事件流、handler 解析等多个上下文。
- `FilesController`（580 行）同时做文件系统访问、git diff/reset、搜索策略、路径安全。

这会导致测试难、变更风险高、阅读成本高。

证据：

- `src/backend/Agw.Agents/Application/AgentRun/AgentRuntimeService.cs`
- `src/backend/Agw.A2A/AgwA2ARequestHandler.cs`
- `src/backend/Agw.Tasks/Controllers/FilesController.cs`

建议：按“查询/命令/安全/集成”拆分服务与控制器。


---


## 建议的治理顺序（两周可落地）

1. **先修 P0**：统一异常模型 + 路径安全白名单校验。
2. **再拆 P1**：拆分 `FilesController`（最容易落地），随后拆 `AgentRuntimeService`。
3. **最后清理 P2**：命名修正、目录扁平化、TODO 清零。
