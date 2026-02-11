using System.Text.Json.Nodes;

namespace CodexSdk;

internal sealed class OutputSchemaFile : IAsyncDisposable
{
    public string? SchemaPath { get; private init; }
    private string? SchemaDirectory { get; init; }

    public static async Task<OutputSchemaFile> CreateAsync(JsonObject? schema, CancellationToken cancellationToken)
    {
        if (schema is null)
        {
            return new OutputSchemaFile();
        }

        var schemaDir = Path.Combine(Path.GetTempPath(), $"codex-output-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(schemaDir);

        var schemaPath = Path.Combine(schemaDir, "schema.json");

        try
        {
            await File.WriteAllTextAsync(schemaPath, schema.ToJsonString(), cancellationToken);
            return new OutputSchemaFile
            {
                SchemaPath = schemaPath,
                SchemaDirectory = schemaDir,
            };
        }
        catch
        {
            try
            {
                Directory.Delete(schemaDir, recursive: true);
            }
            catch
            {
                // suppress cleanup exceptions
            }

            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(SchemaDirectory))
        {
            try
            {
                Directory.Delete(SchemaDirectory, recursive: true);
            }
            catch
            {
                // suppress cleanup exceptions
            }
        }

        return ValueTask.CompletedTask;
    }
}
