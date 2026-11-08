using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal sealed class ExtensionEntityFrameworkMetricsLifetime : IDisposable
{
    private const string MeterName = "Microsoft.EntityFrameworkCore";
    private Meter? _meter;

    private ExtensionEntityFrameworkMetricsLifetime(Meter? meter)
    {
        _meter = meter;
    }

    public static ExtensionEntityFrameworkMetricsLifetime Create()
    {
#pragma warning disable EF9101
        if (AssemblyLoadContext.GetLoadContext(typeof(EntityFrameworkMetricsData).Assembly)?.IsCollectible != true)
        {
            return new ExtensionEntityFrameworkMetricsLifetime(null);
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
        try
        {
            // EF9101: This public type triggers meter initialization; recheck this initializer when upgrading EF Core.
            // EF9101：该公开类型用于触发指标初始化；升级 EF Core 时需重新核对初始化过程。
            RuntimeHelpers.RunClassConstructor(typeof(EntityFrameworkMetricsData).TypeHandle);
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

#pragma warning restore EF9101
        if (capturedMeter is null)
        {
            throw new AgwException(
                ErrorCodes.ExtensionLoadFailed,
                "The EF Core metrics meter was initialized before the extension generation could own it."
            );
        }

        return new ExtensionEntityFrameworkMetricsLifetime(capturedMeter);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _meter, null)?.Dispose();
    }
}
