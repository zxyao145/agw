namespace PiAgentSdk;

/// <summary>Represents a base64-encoded image attached to a Pi prompt.</summary>
public sealed class PiImage
{
    /// <summary>Initializes an image attachment.</summary>
    /// <param name="data">The image bytes encoded as base64 without a data-URI prefix.</param>
    /// <param name="mimeType">The image media type, such as <c>image/png</c>.</param>
    public PiImage(string data, string mimeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        Data = data;
        MimeType = mimeType;
    }

    /// <summary>Gets the base64-encoded image bytes.</summary>
    public string Data { get; }

    /// <summary>Gets the image media type.</summary>
    public string MimeType { get; }
}
