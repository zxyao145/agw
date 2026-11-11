using System.Text.Json;

namespace Agw.Shared.Contracts.Events;

public enum CloudEventDataKind
{
    Json,
    Text,
    Binary,
}

/// <summary>
/// JSON 内容同时提供 Text 与 Json，文本内容使用 Text，二进制内容使用 Binary。
/// JSON uses Text and Json; text uses Text; binary uses Binary.
/// </summary>
public sealed record CloudEventData
{
    public required CloudEventDataKind Kind { get; init; }

    public string? Text { get; init; }

    public JsonElement? Json { get; init; }

    public byte[]? Binary { get; init; }
}
