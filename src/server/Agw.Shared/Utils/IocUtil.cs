using Agw.Shared.Exceptions;
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
            throw new AgwException(ErrorCodes.LoggerFactoryNotSet);
        }
        return LoggerFactory.CreateLogger<T>();
    }

    public static T GetSingletonRequiredService<T>()
        where T : notnull
    {
        if (ServiceProvider == null)
        {
            throw new AgwException(ErrorCodes.ServiceProviderNotSet);
        }
        return ServiceProvider.GetRequiredService<T>();
    }
}
