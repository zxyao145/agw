using System.Diagnostics.Metrics;
using System.Runtime.Loader;
using Agw.Shared.Exceptions;
using Npgsql;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal sealed class ExtensionNpgsqlDataSourceLifetime : IDisposable, IAsyncDisposable
{
    private const string MeterName = "Npgsql";

    private NpgsqlDataSource? _dataSource;
    private Meter? _meter;

    private ExtensionNpgsqlDataSourceLifetime(NpgsqlDataSource dataSource, Meter? meter)
    {
        _dataSource = dataSource;
        _meter = meter;
    }

    public NpgsqlDataSource DataSource =>
        Volatile.Read(ref _dataSource) ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);

    public static ExtensionNpgsqlDataSourceLifetime Create(string connectionString)
    {
        if (AssemblyLoadContext.GetLoadContext(typeof(NpgsqlDataSource).Assembly)?.IsCollectible != true)
        {
            return new ExtensionNpgsqlDataSourceLifetime(new NpgsqlDataSourceBuilder(connectionString).Build(), null);
        }

        Meter? capturedMeter = null;
        bool capturing = false;
        int captureThreadId = Environment.CurrentManagedThreadId;
        using var listener = new MeterListener();
        listener.Start();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (
                capturing
                && Environment.CurrentManagedThreadId == captureThreadId
                && string.Equals(instrument.Meter.Name, MeterName, StringComparison.Ordinal)
            )
            {
                capturedMeter ??= instrument.Meter;
            }
        };

        capturing = true;
        NpgsqlDataSource dataSource;
        try
        {
            dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        }
        catch
        {
            capturedMeter?.Dispose();
            throw;
        }
        finally
        {
            capturing = false;
            listener.InstrumentPublished = null;
        }

        if (capturedMeter is null)
        {
            dataSource.Dispose();
            throw new AgwException(
                ErrorCodes.ExtensionLoadFailed,
                "The private Npgsql metrics meter was initialized before the generation could own it."
            );
        }

        return new ExtensionNpgsqlDataSourceLifetime(dataSource, capturedMeter);
    }

    public void Dispose()
    {
        NpgsqlDataSource? dataSource = Interlocked.Exchange(ref _dataSource, null);
        try
        {
            dataSource?.Dispose();
        }
        finally
        {
            Interlocked.Exchange(ref _meter, null)?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlDataSource? dataSource = Interlocked.Exchange(ref _dataSource, null);
        try
        {
            if (dataSource is not null)
            {
                await dataSource.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _meter, null)?.Dispose();
        }
    }
}
