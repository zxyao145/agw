using System.Runtime.CompilerServices;
using System.Text;

namespace PiAgentSdk.Internal;

internal static class PiJsonlReader
{
    internal const int DefaultMaximumRecordBytes = 4 * 1024 * 1024;

    public static async IAsyncEnumerable<string> ReadLinesAsync(
        Stream stream,
        int maximumRecordBytes = DefaultMaximumRecordBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumRecordBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecordBytes));
        }

        var utf8 = new UTF8Encoding(false, true);
        var buffer = new byte[8192];
        using var record = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var offset = 0;
            while (offset < read)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', offset, read - offset);
                var end = newline < 0 ? read : newline;
                await record.WriteAsync(buffer.AsMemory(offset, end - offset), cancellationToken).ConfigureAwait(false);
                EnsureWithinLimit(record.Length, maximumRecordBytes);
                if (newline < 0)
                {
                    break;
                }

                yield return DecodeRecord(record, utf8);
                record.SetLength(0);
                offset = newline + 1;
            }
        }

        if (record.Length > 0)
        {
            yield return DecodeRecord(record, utf8);
        }
    }

    private static string DecodeRecord(MemoryStream record, Encoding utf8)
    {
        var bytes = record.ToArray();
        var length = bytes.Length;
        if (length > 0 && bytes[length - 1] == (byte)'\r')
        {
            length--;
        }

        try
        {
            return utf8.GetString(bytes, 0, length);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PiProtocolException("Pi RPC output contained invalid UTF-8.", exception);
        }
    }

    private static void EnsureWithinLimit(long length, int maximumRecordBytes)
    {
        if (length > maximumRecordBytes)
        {
            throw new PiProtocolException($"Pi RPC record exceeded {maximumRecordBytes} bytes.");
        }
    }
}
