using Agw.Shared.Utils;
using Microsoft.Extensions.AI;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Agw.Shared.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AgwTextContent), "TextContent")]
[JsonDerivedType(typeof(AgwTextReasoningContent), "TextReasoningContent")]
[JsonDerivedType(typeof(AgwFunctionCallContent), "FunctionCallContent")]
[JsonDerivedType(typeof(AgwFunctionResultContent), "FunctionResultContent")]
[JsonDerivedType(typeof(AgwErrorContent), "ErrorContent")]
[JsonDerivedType(typeof(AgwUsageContent), "UsageContent")]
[JsonDerivedType(typeof(AgwUriContent), "UriContent")]
public abstract class AgwContent
{
    public abstract string Kind { get; }

    public AdditionalPropertiesDictionary? AdditionalProperties { get; set; }
}

public class AgwTextContent : AgwContent
{
    public override string Kind => AiMessageContentType.TextContent;

    public string? Content { get; set; }
}

public class AgwTextReasoningContent : AgwContent
{
    public override string Kind => AiMessageContentType.TextReasoningContent;

    public string? Content { get; set; }
}

public class AgwFunctionCallContent : AgwContent
{
    public override string Kind => AiMessageContentType.FunctionCallContent;

    public string? Content { get; set; }
}

public class AgwFunctionResultContent : AgwContent
{
    public override string Kind => AiMessageContentType.FunctionResultContent;

    public string? Content { get; set; }
}

public class AgwErrorContent : AgwContent
{
    public override string Kind => AiMessageContentType.ErrorContent;

    public string? ErrorCode { get; set; }

    public string? Details { get; set; }

    // message
    public string Content { get; set; } = default!;
}

public class AgwUsageContent : AgwContent
{
    public override string Kind => AiMessageContentType.UsageContent;

    public UsageDetails Content { get; set; } = default!;
}

public class AgwUriContent : AgwContent
{
    public override string Kind => AiMessageContentType.UriContent;

    private Uri _uri;

    private string _mediaType;

    public Uri Uri
    {
        get
        {
            return _uri;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _uri = value;
        }
    }

    private const string DefaultMediaType = "text/plain;charset=US-ASCII";

    public string MediaType
    {
        get
        {
            return _mediaType;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!IsValidMediaType(value))
            {
                ThrowUtil.ArgumentException("MediaType", $"An invalid media type was specified: '{value}'");
            }
            _mediaType = value;
        }
    }

    public AgwUriContent(string uri, string mediaType)
        : this(new Uri(ThrowUtil.IfNull(uri, "uri")), mediaType)
    {
    }

    [JsonConstructor]
    public AgwUriContent(Uri uri, string mediaType)
    {
        _uri = ThrowUtil.IfNull(uri, "uri");
        _mediaType = ThrowIfInvalidMediaType(mediaType, "mediaType");
    }

    private static string ThrowIfInvalidMediaType(string mediaType, [CallerArgumentExpression("mediaType")] string parameterName = "")
    {
        ThrowUtil.IfNullOrWhitespace(mediaType, parameterName);
        if (!IsValidMediaType(mediaType))
        {
            ThrowUtil.ArgumentException(parameterName, "An invalid media type was specified: '" + mediaType + "'");
        }

        return mediaType;
    }

    private static bool IsValidMediaType(string mediaType) => IsValidMediaType(mediaType.AsSpan(), ref mediaType);

    private static bool IsValidMediaType(ReadOnlySpan<char> mediaTypeSpan, [NotNull] ref string? mediaType)
    {
        string? knownType = mediaTypeSpan switch
        {
            DefaultMediaType => DefaultMediaType,
            "application/json" => "application/json",
            "application/octet-stream" => "application/octet-stream",
            "application/pdf" => "application/pdf",
            "application/xml" => "application/xml",
            "audio/mpeg" => "audio/mpeg",
            "audio/ogg" => "audio/ogg",
            "audio/wav" => "audio/wav",
            "image/apng" => "image/apng",
            "image/avif" => "image/avif",
            "image/bmp" => "image/bmp",
            "image/gif" => "image/gif",
            "image/jpeg" => "image/jpeg",
            "image/png" => "image/png",
            "image/svg+xml" => "image/svg+xml",
            "image/tiff" => "image/tiff",
            "image/webp" => "image/webp",
            "text/css" => "text/css",
            "text/csv" => "text/csv",
            "text/html" => "text/html",
            "text/javascript" => "text/javascript",
            "text/plain" => "text/plain",
            "text/plain;charset=UTF-8" => "text/plain;charset=UTF-8",
            "text/xml" => "text/xml",
            _ => null,
        };

        if (knownType is not null)
        {
            mediaType = knownType;
            return true;
        }

        mediaType ??= mediaTypeSpan.ToString();
        return MediaTypeHeaderValue.TryParse(mediaType, out _);
    }
}
