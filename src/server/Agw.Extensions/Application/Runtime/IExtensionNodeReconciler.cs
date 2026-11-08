namespace Agw.Extensions.Application.Runtime;

public sealed record ExtensionRuntimeError(int Code, string Message);

public interface IExtensionNodeReconciler
{
    void RequestReconciliation(Guid extensionId);

    ExtensionRuntimeError? GetError(Guid extensionId);
}
