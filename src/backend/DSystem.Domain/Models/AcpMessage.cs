using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSystem.Domain.Models;

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class KebabCaseLowerJsonStringEnumConverter<TEnum>() :
    JsonStringEnumConverter<TEnum>(JsonNamingPolicy.KebabCaseLower)
    where TEnum : struct, Enum;


public class AcpMessage
{

    /// <summary>
    /// The type of this object, used as a discriminator. Always 'message' for a Message.
    /// </summary>
    public string Kind { get; set; } = "message";

    /// <summary>
    /// Message sender's role.
    /// </summary>
    [JsonPropertyName("role")]
    [JsonRequired]
    public string Role { get; set; } = "user";

    /// <summary>
    /// Message content.
    /// </summary>
    [JsonPropertyName("parts")]
    [JsonRequired]
    public List<IAcpPart> Parts { get; set; } = [];

    /// <summary>
    /// Extension metadata.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    /// <summary>
    /// List of tasks referenced as context by this message.
    /// </summary>
    [JsonPropertyName("referenceTaskIds")]
    public List<string>? ReferenceTaskIds { get; set; }

    /// <summary>
    /// Identifier created by the message creator.
    /// </summary>
    [JsonPropertyName("messageId")]
    [JsonRequired]
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of task the message is related to.
    /// </summary>
    [JsonPropertyName("taskId")]
    public string? TaskId { get; set; }

    /// <summary>
    /// The context the message is associated with.
    /// </summary>
    [JsonPropertyName("contextId")]
    public string? ContextId { get; set; }

    /// <summary>
    /// The URIs of extensions that are present or contributed to this Message.
    /// </summary>
    [JsonPropertyName("extensions")]
    public List<string>? Extensions { get; set; }
}

public interface IAcpPart
{
    string Kind { get; }
    Dictionary<string, object>? Metadata { get; set; }
}

public class AcpTextPart : IAcpPart
{
    public string Kind { get; } = "text";
    public Dictionary<string, object>? Metadata { get; set; }
    public string Text { get; set; } = null!;
}

public class AcpFilePart : IAcpPart
{
    public string Kind { get; } = "file";
    public Dictionary<string, object>? Metadata { get; set; }
    public IAcpFile File { get; set; } = null!;
}

public interface IAcpFile
{
    string? MimeType { get; set; }
    string? Name { get; set; }
}

public class AcpFileWithBytes : IAcpFile
{
    public string Bytes { get; set; } = null!;
    public string? MimeType { get; set; }
    public string? Name { get; set; }
}

public class AcpFileWithUri : IAcpFile
{
    public string Uri { get; set; } = null!;
    public string? MimeType { get; set; }
    public string? Name { get; set; }
}

public class AcpDataPart : IAcpPart
{
    public string Kind { get; } = "data";
    public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
    public Dictionary<string, object>? Metadata { get; set; }
}