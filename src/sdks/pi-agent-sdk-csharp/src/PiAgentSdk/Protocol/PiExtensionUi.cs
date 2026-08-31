using System.Text.Json;

namespace PiAgentSdk;

/// <summary>Represents a dialog or fire-and-forget UI request emitted by a Pi extension.</summary>
public sealed class PiExtensionUiRequest
{
    /// <summary>Gets the request identifier used to correlate dialog responses.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the UI method, such as <c>select</c>, <c>confirm</c>, or <c>notify</c>.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>Gets the optional UI title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the optional message presented to the user.</summary>
    public string? Message { get; init; }

    /// <summary>Gets the allowed values for a <c>select</c> dialog.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>Gets placeholder text for an input dialog.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Gets the initial value for an input or editor dialog.</summary>
    public string? Prefill { get; init; }

    /// <summary>Gets the dialog timeout in milliseconds.</summary>
    public int? Timeout { get; init; }

    /// <summary>Gets the notification severity or category.</summary>
    public string? NotifyType { get; init; }

    /// <summary>Gets the status item key for a status update.</summary>
    public string? StatusKey { get; init; }

    /// <summary>Gets the status item text.</summary>
    public string? StatusText { get; init; }

    /// <summary>Gets the widget key for a widget update.</summary>
    public string? WidgetKey { get; init; }

    /// <summary>Gets lines displayed by a widget update.</summary>
    public IReadOnlyList<string>? WidgetLines { get; init; }

    /// <summary>Gets the requested widget placement.</summary>
    public string? WidgetPlacement { get; init; }

    /// <summary>Gets text supplied by title or editor-text updates.</summary>
    public string? Text { get; init; }

    /// <summary>Gets a value indicating whether this method requires a correlated response.</summary>
    public bool IsDialog => Method is "select" or "confirm" or "input" or "editor";
}

/// <summary>Represents the correlated response to a blocking Pi Extension UI dialog.</summary>
public sealed class PiExtensionUiResponse
{
    /// <summary>Gets the request identifier being answered.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the selected, input, or edited string value.</summary>
    public string? Value { get; init; }

    /// <summary>Gets the answer to a confirmation dialog.</summary>
    public bool? Confirmed { get; init; }

    /// <summary>Gets a value indicating whether the dialog was cancelled.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Creates a cancelled response for the specified request.</summary>
    /// <param name="id">The request identifier being cancelled.</param>
    /// <returns>A cancelled Extension UI response.</returns>
    public static PiExtensionUiResponse Cancel(string id) => new() { Id = id, Cancelled = true };

    internal JsonElement ToJson()
    {
        var payload = new Dictionary<string, object?> { ["type"] = "extension_ui_response", ["id"] = Id };
        if (Cancelled)
        {
            payload["cancelled"] = true;
        }
        else if (Confirmed.HasValue)
        {
            payload["confirmed"] = Confirmed.Value;
        }
        else
        {
            payload["value"] = Value;
        }

        return JsonSerializer.SerializeToElement(payload, PiProtocolJson.Options);
    }
}
