
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Shared.Utils;

public class IocUtil
{
    public static ILoggerFactory LoggerFactory { get; private set; } = default!;
    public static IServiceProvider ServiceProvider { get; private set; } = default!;

    public IocUtil(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        LoggerFactory = loggerFactory;
        ServiceProvider = serviceProvider;
    }

    public static ILogger<T> CreateLogger<T>()
    {
        if (LoggerFactory == null)
        {
            throw new InvalidOperationException("LoggerFactory is not set.");
        }
        return LoggerFactory.CreateLogger<T>();
    }


    public static T GetSingletonRequiredService<T>() where T : notnull
    {
        if (ServiceProvider == null)
        {
            throw new InvalidOperationException("ServiceProvider is not set.");
        }
        return ServiceProvider.GetRequiredService<T>();
    }
}
