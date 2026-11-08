using Agw.Extensions.Contracts;
using Agw.Settings.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Extensions.Infrastructure.Runtime;

public sealed class ExtensionAdmissionValidator : IExtensionAdmissionValidator
{
    private readonly ISettingsStore _settings;

    public ExtensionAdmissionValidator(ISettingsStore settings)
    {
        _settings = settings;
    }

    public async Task ValidateEnabledAsync(
        IReadOnlyList<Guid> extensionIds,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(extensionIds);
        foreach (var extensionId in extensionIds.Distinct().Order())
        {
            if (extensionId == Guid.Empty)
                throw new AgwException(ErrorCodes.InvalidParam, "An extension id is required for admission.");

            var state = await _settings.GetGlobalForShareAsync<ExtensionEnablementSetting>(
                $"extensions.enabled.{extensionId:D}",
                cancellationToken
            );
            if (state?.Value.Enabled != true)
                throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }
    }

    private sealed class ExtensionEnablementSetting
    {
        public ExtensionEnablementSetting() { }

        public bool Enabled { get; init; }
    }
}
