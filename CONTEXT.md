# Agw Domain Context

## Control Plane

The Host role that owns setup, authentication management, the Web UI, management endpoints, and Events scheduling and delivery workers. In a split deployment, Events delivery submits background executions for Data Plane workers to claim.

## Data Plane

The Host role that exposes the SignalR Execution and A2A protocol endpoints and runs durable Agent execution workers. It consumes shared configuration, Data Protection keys, PostgreSQL state, and Project Workspaces, but does not expose management endpoints or perform setup.

## Standalone Host

The combined Host that composes the Control Plane and Data Plane assemblies. It defaults to InProcess execution and supports SQLite or PostgreSQL; existing full-server Cluster deployments may continue to use its Distributed execution configuration.

## Durable Execution

A PostgreSQL-owned execution lifecycle with replayable output through PostgreSQL or Redis. SignalR, Events delivery, and A2A register work in the durable execution state. Data Plane workers claim and run that work.

## Module DbContext

The owner Module's inward persistence interface. Agents, Projects, Auth, Events, Integrations, Notifications, Providers, Settings, Skills, and Tools expose their owned `DbSet` values and save operations. Agents and Projects also provide generation-checked `SaveConversationChangesAsync`. A single scoped `AgwDbContext` implements all ten interfaces. Jobs manages its records through `IEventJobFacade`.

## Conversation Kind

The origin recorded on a Project Conversation at creation. A `native` conversation has an empty `relate_id`. An `event:<source kind>` conversation has the triggering event ID in `relate_id`. A Job creates an `event:timer` conversation.

## Creator Extension

The creator recorded on a Project Conversation (`extension_id`): `host` when Desktop, Web, Mobile, a Job or A2A created it, or the id (a lowercase GUID v7) of the Desktop extension whose execution session sent its first turn. `host` is reserved, so no extension name can use it. The host may write every conversation; an extension may write only the conversations it created, open conversations and new ones.

## Open Conversation

A host-created conversation whose records were never cleared and that has no accepted turn, for example one whose first turn failed before acceptance. Every extension may write it, and the first extension that does becomes its creator.

## Agents Execution Assembly

`Agw.Agents.Execution` contains the runtime, external-agent adapters, SignalR transport, and durable workers. It and `Agw.Agents` form one logical Agents module. Execution references Agents one-way; Host composes both registrations. Shared data ownership and persistence seams stay with Agents.
