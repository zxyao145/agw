# Agw Domain Context

## Control Plane

The Host role that owns setup, authentication management, the Web UI, management endpoints, and Job scheduling. In a split deployment it submits durable executions but does not run Agent execution workers or expose Execution/A2A endpoints.

## Data Plane

The Host role that exposes the SignalR Execution and A2A protocol endpoints and runs durable Agent execution workers. It consumes shared configuration, Data Protection keys, PostgreSQL state, and Project Workspaces, but does not expose management endpoints or perform setup.

## Standalone Host

The combined Host that composes the Control Plane and Data Plane assemblies. It defaults to InProcess execution and supports SQLite or PostgreSQL; existing full-server Cluster deployments may continue to use its Distributed execution configuration.

## Durable Execution

A PostgreSQL-owned execution lifecycle with replayable output through PostgreSQL or Redis. SignalR, distributed Jobs, and distributed A2A submit work through the same execution interface, while Data Plane workers claim and run the persisted work.

## Module DbContext

The owner Module's inward persistence interface. Agents, Projects, Jobs, Auth, Integrations, Providers, Skills, and Tools expose only their owned `DbSet` values plus `SaveChangesAsync`. A single scoped `AgwDbContext` implements every Module DbContext; this does not imply separate databases or separate EF Core contexts.
