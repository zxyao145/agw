namespace Agw.Extensions.Sdk;

public interface IExtensionBackgroundService
{
    ValueTask StartAsync(
        IExtensionContext context,
        IExtensionBackgroundScheduler scheduler,
        CancellationToken cancellationToken
    );

    ValueTask PauseAsync(CancellationToken cancellationToken);

    ValueTask ResumeAsync(CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

public interface IExtensionBackgroundScheduler
{
    ValueTask ExecuteAsync(
        Func<IExtensionContext, CancellationToken, ValueTask> work,
        CancellationToken cancellationToken = default
    );

    ValueTask<TResult> ExecuteAsync<TResult>(
        Func<IExtensionContext, CancellationToken, ValueTask<TResult>> work,
        CancellationToken cancellationToken = default
    );
}
