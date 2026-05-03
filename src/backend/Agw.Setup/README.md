# Agw.Setup

`Agw.Setup` provides the first-run setup UI and initialization guards for the Agw backend host. It is a Razor-enabled ASP.NET Core module that lets a new instance choose database settings, seed the database, persist initialization state, and optionally require an API key for `/api` requests.

## Module Responsibilities

- Expose the `/setup` Razor page before the system is initialized.
- Persist setup output to `appsettings.setup.json` in the host content root.
- Seed the configured database through `Agw.Infrastructure`.
- Block normal host traffic until setup is complete.
- Optionally require the `X-API-Key` header for `/api` routes after setup.

## Project Structure

```text
Agw.Setup/
+-- Contracts/      # Setup request and initialization settings contracts
+-- Controllers/    # /setup MVC controller
+-- Middleware/     # Initialization and API key request guards
+-- Services/       # Setup initialization and JSON-backed state store
`-- Views/          # Razor setup page and layout
```

## Runtime Wiring

The module is loaded by `src/backend/Agw.Host/Program.cs`.

Host configuration adds the generated setup file as an optional reloadable source:

```csharp
builder.Configuration.AddJsonFile("appsettings.setup.json", optional: true, reloadOnChange: true);
```

MVC discovers the setup controller through an application part:

```csharp
.AddApplicationPart(typeof(SetupController).Assembly)
```

Services are registered through:

```csharp
.AddSetup(builder.Configuration)
```

The request pipeline uses the setup middleware before file endpoint exception mapping:

```csharp
app.UseMiddleware<InitializationGuardMiddleware>();
app.UseMiddleware<ApiKeyGuardMiddleware>();
```

## Initialization Flow

1. `SystemInitialization:IsInitialized` starts as `false` in `src/backend/Agw.Host/appsettings.json`.
2. When the host is not initialized:
   - `GET` requests outside `/setup` redirect to `/setup`.
   - `/api`, `/openapi`, and `/scalar` requests return `403`.
   - non-GET requests outside `/setup` return `403`.
3. `GET /setup` renders the setup form with default SQLite values.
4. `POST /setup` validates `SetupRequest`, configures an `AgwDbContext`, runs `DbSeeder.SeedAsync()`, and persists setup state.
5. After initialization, `/setup` returns `404` and normal host routes continue.

The setup form currently offers SQLite, MySQL, and PostgreSQL options. `SetupInitializationService` maps `postgres` and `postgresql` to Npgsql, `mysql` to MySQL, and all other provider values to SQLite.

## Usage

Start the Agw host from the repository root:

```bash
dotnet run --project src/backend/Agw.Host
```

Open the setup page before the system is initialized:

```text
http://localhost:5015/setup
```

Fill in the setup form:

- `Provider`: choose `sqlite`, `mysql`, or `postgresql`.
- `ConnectionString`: use `Data Source=agw.db` for the default local SQLite database, or provide a full MySQL/PostgreSQL connection string.
- `ApiKey`: leave empty to disable the global API key guard, or enter a value to require `X-API-Key` on `/api` requests.

Submit the form to seed the database and write `appsettings.setup.json`. After a successful initialization, the controller redirects to `/`, and `/setup` is no longer available.

When an API key is configured, include it on API requests:

```bash
curl -H "X-API-Key: <configured-api-key>" "http://localhost:5015/api/your-route"
```

## Generated Configuration

`JsonInitializationStateStore` writes `appsettings.setup.json` to the host content root. The file contains the selected database settings and initialization state:

```json
{
  "database": {
    "provider": "sqlite",
    "connectionString": "Data Source=agw.db"
  },
  "systemInitialization": {
    "isInitialized": true,
    "apiKey": null
  }
}
```

The repository `.gitignore` excludes `appsettings.setup.json`, `agw.db`, SQLite sidecar files, and `logs/`. Keep connection strings and API keys out of committed configuration.

## API Key Guard

If setup stores a non-empty API key, `ApiKeyGuardMiddleware` requires every `/api` request to include:

```text
X-API-Key: <configured-api-key>
```

The guard only applies to `/api` paths. If the system is not initialized, or no API key is configured, the middleware lets the request continue to the next middleware.

## Development

Run backend commands from the repository root:

```bash
dotnet restore Agw.slnx
dotnet build Agw.slnx
dotnet run --project src/backend/Agw.Host
```

The development host uses `http://localhost:5015` by default. Visit `http://localhost:5015/setup` before initialization.

There is no dedicated `Agw.Setup` test project in the repository at this time. Use the normal backend suite for broad verification:

```bash
dotnet test Agw.slnx
```
