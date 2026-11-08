namespace Agw.Extensions.Contracts;

public interface IExtensionAdmissionValidator
{
    Task ValidateEnabledAsync(IReadOnlyList<Guid> extensionIds, CancellationToken cancellationToken = default);
}
