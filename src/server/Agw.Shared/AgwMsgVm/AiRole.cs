using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Agw.Shared.AgwMsgVm;

/// <summary>
/// copy from ChatRole
/// https://github.com/dotnet/extensions/blob/main/src/Libraries/Microsoft.Extensions.AI.Abstractions/ChatCompletion/ChatRole.cs
/// </summary>
[JsonConverter(typeof(Converter))]
[DebuggerDisplay("{Value,nq}")]
public readonly struct AiRole : IEquatable<AiRole>
{
    /// <summary>Gets the role that instructs or sets the behavior of the system.</summary>
    public static AiRole System { get; } = new("system");

    /// <summary>Gets the role that provides responses to system-instructed, user-prompted input.</summary>
    public static AiRole Assistant { get; } = new("assistant");

    /// <summary>Gets the role that provides user input for chat interactions.</summary>
    public static AiRole User { get; } = new("user");

    /// <summary>Gets the role that provides additional information and references in response to tool use requests.</summary>
    public static AiRole Tool { get; } = new("tool");

    public static AiRole Empty { get; } = new("");

    /// <summary>
    /// Gets the value associated with this <see cref="AiRole"/>.
    /// </summary>
    /// <remarks>
    /// The value will be serialized into the "role" message field of the Chat Message format.
    /// </remarks>
    public string Value { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AiRole"/> struct with the provided value.
    /// </summary>
    /// <param name="value">The value to associate with this <see cref="AiRole"/>.</param>
    [JsonConstructor]
    public AiRole(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value.Trim();
    }

    public static bool operator ==(AiRole left, AiRole right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(AiRole left, AiRole right)
    {
        return !(left == right);
    }

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is AiRole otherRole && Equals(otherRole);

    /// <inheritdoc/>
    public bool Equals(AiRole other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    /// <inheritdoc/>
    public override string ToString() => Value;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class Converter : JsonConverter<AiRole>
    {
        /// <inheritdoc />
        public override AiRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString()!);

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, AiRole value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer, nameof(writer));
            writer.WriteStringValue(value.Value);
        }
    }

    // operator implicit
    public static implicit operator AiRole(string value)
    {
        return new AiRole(value);
    }

    public static implicit operator string(AiRole aiRole)
    {
        return aiRole.Value;
    }

    public static implicit operator AiRole(ChatRole chatRole)
    {
        return new AiRole(chatRole.Value);
    }
}
