using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Shared.Utils;

/// <summary>
/// <para>静态 Service Locator，已弃用：服务依赖通过显式构造函数注入获取。</para>
/// <para>Static Service Locator, obsolete: resolve dependencies through explicit constructor injection.</para>
/// </summary>
[Obsolete("IocUtil is a static Service Locator without callers; resolve dependencies through constructor injection.")]
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
