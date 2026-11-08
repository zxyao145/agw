namespace Agw.Extensions.Contracts;

public interface IExtensionDatabase
{
    Task ValidateModelAsync(ExtensionGenerationId generationId, CancellationToken cancellationToken = default);

    Task PrepareAsync(ExtensionGenerationId generationId, CancellationToken cancellationToken = default);
}
