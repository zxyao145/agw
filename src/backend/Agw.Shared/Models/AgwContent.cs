using System.Buffers;
using System.Buffers.Text;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

using Agw.Shared.Utils;

using Microsoft.Extensions.AI;

namespace Agw.Shared.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AgwTextContent), "TextContent")]
[JsonDerivedType(typeof(AgwTextReasoningContent), "TextReasoningContent")]
[JsonDerivedType(typeof(AgwFunctionCallContent), "FunctionCallContent")]
[JsonDerivedType(typeof(AgwFunctionResultContent), "FunctionResultContent")]
[JsonDerivedType(typeof(AgwErrorContent), "ErrorContent")]
[JsonDerivedType(typeof(AgwUsageContent), "UsageContent")]
[JsonDerivedType(typeof(AgwUriContent), "UriContent")]
[JsonDerivedType(typeof(AgwDataContent), "DataContent")]
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

    public string MediaType
    {
        get
        {
            return _mediaType;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!AgwDataUriParser.IsValidMediaType(value))
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
        _mediaType = AgwDataUriParser.ThrowIfInvalidMediaType(mediaType, "mediaType");
    }
}

internal class AgwDataUriParser
{
    public static string Scheme => "data:";

    public const string DefaultMediaType = "text/plain;charset=US-ASCII";

    public static string ThrowIfInvalidMediaType(string mediaType, [CallerArgumentExpression("mediaType")] string parameterName = "")
    {
        ThrowUtil.IfNullOrWhitespace(mediaType, parameterName);
        if (!IsValidMediaType(mediaType))
        {
            ThrowUtil.ArgumentException(parameterName, "An invalid media type was specified: '" + mediaType + "'");
        }

        return mediaType;
    }

    public static bool IsValidMediaType(string mediaType) => IsValidMediaType(mediaType.AsSpan(), ref mediaType);

    public static bool IsValidMediaType(ReadOnlySpan<char> mediaTypeSpan, [NotNull] ref string? mediaType)
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

    public sealed class AgwDataUri(ReadOnlyMemory<char> data, bool isBase64, string? mediaType)
    {
        public string? MediaType { get; } = mediaType;

        public ReadOnlyMemory<char> Data { get; } = data;

        public bool IsBase64 { get; } = isBase64;

        public byte[] ToByteArray() => IsBase64 ?
            Convert.FromBase64String(Data.ToString()) :
            Encoding.UTF8.GetBytes(WebUtility.UrlDecode(Data.ToString()));
    }

    public static AgwDataUri Parse(ReadOnlyMemory<char> dataUri)
    {
        // Validate, then trim off the "data:" scheme.
        if (!dataUri.Span.StartsWith(Scheme.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            throw new UriFormatException("Invalid data URI format: the data URI must start with 'data:'.");
        }

        dataUri = dataUri.Slice(Scheme.Length);

        // Find the comma separating the metadata from the data.
        int commaPos = dataUri.Span.IndexOf(',');
        if (commaPos < 0)
        {
            throw new UriFormatException("Invalid data URI format: the data URI must contain a comma separating the metadata and the data.");
        }

        ReadOnlyMemory<char> metadata = dataUri.Slice(0, commaPos);

        ReadOnlyMemory<char> data = dataUri.Slice(commaPos + 1);
        bool isBase64 = false;

        // Determine whether the data is Base64-encoded or percent-encoded (Uri-encoded).
        // If it's base64-encoded, validate it. If it's Uri-encoded, there's nothing to validate,
        // as WebUtility.UrlDecode will successfully decode any input with no sequence considered invalid.
        if (metadata.Span.EndsWith(";base64".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            metadata = metadata.Slice(0, metadata.Length - ";base64".Length);
            isBase64 = true;
            if (!IsValidBase64Data(data.Span))
            {
                throw new UriFormatException("Invalid data URI format: the data URI is base64-encoded, but the data is not a valid base64 string.");
            }
        }

        // Validate the media type, if present.
        // Per RFC 2397, if the media type is omitted, it defaults to "text/plain;charset=US-ASCII".
        ReadOnlySpan<char> span = metadata.Span.Trim();
        string? mediaType = null;
        if (span.IsEmpty)
        {
            mediaType = DefaultMediaType;
        }
        else if (!IsValidMediaType(span, ref mediaType))
        {
            throw new UriFormatException("Invalid data URI format: the media type is not a valid.");
        }

        return new AgwDataUri(data, isBase64, mediaType);
    }

    private static bool IsValidBase64Data(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return true;
        }

        return Base64.IsValid(value) && !value.ContainsAny(" \t\r\n");
    }
}

public class AgwDataContent : AgwContent
{
    public override string Kind => AiMessageContentType.DataContent;

    private readonly AgwDataUriParser.AgwDataUri? _dataUri;

    private string? _uri;

    //
    // ummary:
    //     The data, lazily initialized if the data is provided in a data URI.
    private ReadOnlyMemory<byte>? _data;

    private const string DefaultMediaType = "application/octet-stream";

    [StringSyntax("Uri")]
    [Description("A data URI representing the content.")]
    public string Uri
    {
        get
        {
            if (_uri == null)
            {
                ReadOnlyMemory<byte> valueOrDefault = _data.GetValueOrDefault();
                char[] array = ArrayPool<char>.Shared.Rent("data:".Length + MediaType.Length + ";base64,".Length + Base64.GetMaxEncodedToUtf8Length(valueOrDefault.Length));
                Span<char> span = array.AsSpan();
                Span<char> destination = span;
                bool shouldAppend;
                MemoryExtensions.TryWriteInterpolatedStringHandler handler = new MemoryExtensions.TryWriteInterpolatedStringHandler(13, 1, span, out shouldAppend);
                if (shouldAppend && handler.AppendLiteral("data:") && handler.AppendFormatted(MediaType))
                {
                    handler.AppendLiteral(";base64,");
                }
                else
                    _ = 0;
                bool flag = destination.TryWrite(ref handler, out var charsWritten);
                flag |= Convert.TryToBase64Chars(valueOrDefault.Span, array.AsSpan(charsWritten), out var charsWritten2);
                _uri = array.AsSpan(0, charsWritten + charsWritten2).ToString();
                ArrayPool<char>.Shared.Return(array);
            }

            return _uri;
        }
    }

    [JsonIgnore]
    public string MediaType { get; }

    public string? Name { get; set; }

    [JsonIgnore]
    public ReadOnlyMemory<byte> Data
    {
        get
        {
            ReadOnlyMemory<byte>? data = _data;
            if (!data.HasValue)
            {
                _data = _dataUri?.ToByteArray();
            }

            return _data.GetValueOrDefault();
        }
    }

    public AgwDataContent(Uri uri, string? mediaType = null)
        : this(ThrowUtil.IfNull(uri, "uri").ToString(), mediaType)
    {
    }

    [JsonConstructor]
    public AgwDataContent([StringSyntax("Uri")] string uri, string? mediaType = null)
    {
        _uri = ThrowUtil.IfNullOrWhitespace(uri, "uri");
        if (!uri.StartsWith(AgwDataUriParser.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            ThrowUtil.ArgumentException("uri", "The provided URI is not a data URI.");
        }

        _dataUri = AgwDataUriParser.Parse(uri.AsMemory());
        if (mediaType == null)
        {
            mediaType = _dataUri.MediaType;
        }

        if (mediaType == null)
        {
            ThrowUtil.ArgumentNullException("mediaType", "uri did not contain a media type, and mediaType was not provided.");
        }

        MediaType = AgwDataUriParser.ThrowIfInvalidMediaType(mediaType, "mediaType");
        if (!_dataUri.IsBase64 || mediaType != _dataUri.MediaType)
        {
            _data = _dataUri.ToByteArray();
            _dataUri = null;
            _uri = null;
        }
    }

    public AgwDataContent(ReadOnlyMemory<byte> data, string mediaType)
    {
        MediaType = AgwDataUriParser.ThrowIfInvalidMediaType(mediaType, "mediaType");
        _data = data;
    }
}
