# Execution Coordinator Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move task resolution and streaming execution startup out of `AgentExecutionsController` into a DI-managed execution coordinator that can be reused by both WebSocket and HTTP execution paths.

**Architecture:** Introduce `IAgentExecutionCoordinator` to own setting normalization, `ProjectTask` resolution/creation, and agent/agentflow streaming startup. Update command strategies and the HTTP execute path to call the coordinator through dependency injection so the controller becomes transport-focused.

**Tech Stack:** ASP.NET Core DI, xUnit v3, existing `AgentRuntimeService`, `AgentflowRuntimeService`, `ITaskAppService`, `IProjectAppService`

---

### Task 1: Add failing tests for the coordinator contract

**Files:**
- Create: `tests/Agw.Agents.Tests/AgentExecutionCoordinatorTests.cs`
- Modify: `tests/Agw.Agents.Tests/ExecutionCommandStrategiesTests.cs`

- [ ] **Step 1: Write the failing tests**
- [ ] **Step 2: Run build/test to verify missing coordinator types fail**
- [ ] **Step 3: Add minimal coordinator contract and adapt strategy tests**
- [ ] **Step 4: Run targeted tests to verify they pass**

### Task 2: Implement the DI-managed coordinator

**Files:**
- Create: `src/backend/Agw.Agents/Controllers/IAgentExecutionCoordinator.cs`
- Create: `src/backend/Agw.Agents/Controllers/AgentExecutionCoordinator.cs`
- Modify: `src/backend/Agw.Agents/DependencyInjection.cs`

- [ ] **Step 1: Implement `NormalizeSettingsAsync` and `ResolveTaskAsync`**
- [ ] **Step 2: Implement `StartStreamingExecutionAsync` including session reuse logic**
- [ ] **Step 3: Register coordinator and command dispatcher/strategies with DI**
- [ ] **Step 4: Run build/tests**

### Task 3: Rewire controller and strategies to use DI

**Files:**
- Modify: `src/backend/Agw.Agents/Controllers/AgentExecutionsController.cs`
- Modify: `src/backend/Agw.Agents/Controllers/AgentExecutionsController.execute.cs`
- Modify: `src/backend/Agw.Agents/Controllers/ExecutionCommandContext.cs`
- Modify: `src/backend/Agw.Agents/Controllers/ExecCommandStrategy.cs`
- Modify: `src/backend/Agw.Agents/Controllers/SettingCommandStrategy.cs`

- [ ] **Step 1: Remove controller-owned resolve/start logic**
- [ ] **Step 2: Push strategy calls through the coordinator**
- [ ] **Step 3: Switch dispatcher creation from static construction to DI**
- [ ] **Step 4: Run build and tests**
