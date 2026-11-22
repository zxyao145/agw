using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Agw.Shared.Exceptions;

namespace Agw.Tools.Impl.ToolBlocks.Storage;

internal sealed record FileReadContinuation([property: JsonPropertyName("revision")] string Revision)
{
    [JsonPropertyName("codePage")]
    public int CodePage { get; init; }

    [JsonPropertyName("characterOffset")]
    public long CharacterOffset { get; init; }

    [JsonPropertyName("byteOffset")]
    public long ByteOffset { get; init; }

    [JsonPropertyName("lineNumber")]
    public long LineNumber { get; init; }

    [JsonPropertyName("lineHasContent")]
    public bool LineHasContent { get; init; }

    public static FileReadContinuation Start(Encoding encoding) =>
        new(string.Empty)
        {
            CodePage = encoding.CodePage,
            ByteOffset = encoding.Preamble.Length,
            LineNumber = 1,
        };
}

internal sealed record ProjectAgentFileReadPage(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("continuation")] FileReadContinuation? Continuation
);

internal sealed record ProjectAgentFileReadOptions(int StartLine, int? EndLine, bool NumberedLines);

internal static class ProjectAgentFileReadPager
{
    private const int MaximumPageLines = 200;
    internal const int MaximumPageContentCharacters = 16 * 1024;
    private const int Utf8LookaheadBytes = 3;
    private static readonly Encoding DefaultEncoding = new UTF8Encoding(false);

    // 续页保留正文中的 BOM 字符。Resume encodings preserve a BOM character within the file contents.
    private static readonly Encoding[] ResumeEncodings =
    [
        DefaultEncoding,
        new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        new UTF32Encoding(bigEndian: false, byteOrderMark: false),
        new UTF32Encoding(bigEndian: true, byteOrderMark: false),
    ];

    public static StreamReader OpenReader(Stream stream, int? codePage)
    {
        var encoding = codePage is null
            ? DefaultEncoding
            : ResumeEncodings.FirstOrDefault(candidate => candidate.CodePage == codePage)
                ?? throw InvalidContinuation();
        var reader = new StreamReader(
            stream,
            encoding,
            detectEncodingFromByteOrderMarks: codePage is null,
            bufferSize: 4096,
            leaveOpen: true
        );
        if (codePage is null)
        {
            _ = reader.Peek();
        }
        return reader;
    }

    public static async Task<ProjectAgentFileReadPage> ReadPageAsync(
        StreamReader reader,
        ProjectAgentFileReadOptions options,
        FileReadContinuation position,
        CancellationToken cancellationToken
    )
    {
        var input = new BufferedTextReader(reader, position);
        var state = new PageReadState(options, position);
        await ReadPageContentAsync(input, state, cancellationToken).ConfigureAwait(false);
        ValidatePageResult(position.CharacterOffset, state);
        return CreatePageResult(input, state);
    }

    private static async Task ReadPageContentAsync(
        BufferedTextReader input,
        PageReadState state,
        CancellationToken cancellationToken
    )
    {
        while (!state.Stop)
        {
            var chunk = await input.ReadChunkAsync(cancellationToken).ConfigureAwait(false);
            if (chunk.IsEmpty)
            {
                ValidateEndOfFile(state);
                break;
            }

            await ProcessChunkAsync(input, state, chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ProcessChunkAsync(
        BufferedTextReader input,
        PageReadState state,
        ReadOnlyMemory<char> chunk,
        CancellationToken cancellationToken
    )
    {
        var (segmentLength, terminatorLength) = GetLineSegment(chunk);
        if (state.SkipLines)
        {
            await SkipLineAsync(input, state, segmentLength, terminatorLength, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (IsPastEndLine(state))
        {
            state.Stop = true;
            return;
        }

        if (IsPastPageLineLimit(state))
        {
            state.StopWithMoreContent();
            return;
        }

        await AppendPageSegmentAsync(
                input,
                state,
                chunk[..segmentLength],
                segmentLength,
                terminatorLength,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task SkipLineAsync(
        BufferedTextReader input,
        PageReadState state,
        int segmentLength,
        int terminatorLength,
        CancellationToken cancellationToken
    )
    {
        await input.ConsumeAsync(segmentLength, cancellationToken).ConfigureAwait(false);
        if (terminatorLength == 0)
        {
            state.LineHasContent = true;
            return;
        }

        state.LineNumber++;
        state.LineHasContent = false;
        state.SkipLines = state.LineNumber < state.Options.StartLine;
    }

    private static async Task AppendPageSegmentAsync(
        BufferedTextReader input,
        PageReadState state,
        ReadOnlyMemory<char> segment,
        int segmentLength,
        int terminatorLength,
        CancellationToken cancellationToken
    )
    {
        var prefix = GetLinePrefix(state);
        var available = MaximumPageContentCharacters - state.Content.Length - prefix.Length;
        var take = GetSafeTakeLength(segment, available);
        if (take == 0)
        {
            state.StopWithMoreContent();
            return;
        }

        state.StartPageLineIfNeeded();
        state.Content.Append(prefix).Append(segment.Span[..take]);
        await input.ConsumeAsync(take, cancellationToken).ConfigureAwait(false);
        UpdateLineState(input, state, take, segmentLength, terminatorLength);
    }

    private static void UpdateLineState(
        BufferedTextReader input,
        PageReadState state,
        int consumed,
        int segmentLength,
        int terminatorLength
    )
    {
        if (terminatorLength > 0 && consumed == segmentLength)
        {
            state.LineNumber++;
            state.LineHasContent = false;
            state.PageLineStarted = false;
            state.SaveLastCompleteLine(input);
            return;
        }

        state.LineHasContent = true;
        state.PageLineStarted = true;
        if (consumed < segmentLength)
        {
            state.StopWithMoreContent();
        }
    }

    private static string GetLinePrefix(PageReadState state) =>
        state.Options.NumberedLines && !state.PageLineStarted
            ? state.LineNumber.ToString(CultureInfo.InvariantCulture) + "\t"
            : string.Empty;

    private static bool IsPastEndLine(PageReadState state) =>
        state.Options.NumberedLines && state.Options.EndLine is { } endLine && state.LineNumber > endLine;

    private static bool IsPastPageLineLimit(PageReadState state) =>
        !state.PageLineStarted && state.PageLineCount >= MaximumPageLines;

    private static void ValidateEndOfFile(PageReadState state)
    {
        if (state.SkipLines)
        {
            var lineCount = state.LineHasContent ? state.LineNumber : state.LineNumber - 1;
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"startLine {state.Options.StartLine} is out of range (file has {lineCount} lines)."
            );
        }
    }

    private static void ValidatePageResult(long initialCharacterOffset, PageReadState state)
    {
        if (
            state.Options.NumberedLines
            && state.PageLineCount == 0
            && !state.LineHasContent
            && state.LineNumber - 1 < state.Options.StartLine
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"startLine {state.Options.StartLine} is out of range (file has {state.LineNumber - 1} lines)."
            );
        }

        if (initialCharacterOffset > 0 && state.PageLineCount == 0 && !state.HasMore)
        {
            throw InvalidContinuation();
        }
    }

    private static ProjectAgentFileReadPage CreatePageResult(BufferedTextReader input, PageReadState state)
    {
        if (!state.HasMore)
        {
            return new ProjectAgentFileReadPage(state.Content.ToString(), null);
        }
        if (state.LastCompleteLine is { } checkpoint && state.Content.Length > checkpoint.Length)
        {
            state.Content.Length = checkpoint.Length;
            return new ProjectAgentFileReadPage(state.Content.ToString(), checkpoint.Position);
        }

        return new ProjectAgentFileReadPage(state.Content.ToString(), state.GetContinuation(input));
    }

    private static (int SegmentLength, int TerminatorLength) GetLineSegment(ReadOnlyMemory<char> content)
    {
        var terminatorIndex = content.Span.IndexOfAny('\r', '\n');
        if (terminatorIndex < 0)
        {
            return (content.Length, 0);
        }

        var terminatorLength =
            content.Span[terminatorIndex] == '\r'
            && terminatorIndex + 1 < content.Length
            && content.Span[terminatorIndex + 1] == '\n'
                ? 2
                : 1;
        return (terminatorIndex + terminatorLength, terminatorLength);
    }

    private static int GetSafeTakeLength(ReadOnlyMemory<char> content, int maximumCharacters)
    {
        if (maximumCharacters <= 0)
        {
            return 0;
        }

        var take = Math.Min(content.Length, maximumCharacters);
        if (
            take < content.Length
            && (
                content.Span[take - 1] == '\r' && content.Span[take] == '\n'
                || char.IsHighSurrogate(content.Span[take - 1]) && char.IsLowSurrogate(content.Span[take])
            )
        )
        {
            take--;
        }

        return take;
    }

    private static AgwException InvalidContinuation() =>
        new(ErrorCodes.InvalidParam, "Invalid file read continuation.");

    private sealed class PageReadState(ProjectAgentFileReadOptions options, FileReadContinuation position)
    {
        public readonly StringBuilder Content = new(MaximumPageContentCharacters);
        public readonly ProjectAgentFileReadOptions Options = options;
        public long LineNumber = position.LineNumber;
        public bool LineHasContent = position.LineHasContent;
        public bool SkipLines = options.NumberedLines && position.LineNumber < options.StartLine;
        public int PageLineCount;
        public bool PageLineStarted;
        public bool HasMore;
        public bool Stop;
        public PageCheckpoint? LastCompleteLine;

        public void StartPageLineIfNeeded()
        {
            if (!PageLineStarted)
            {
                PageLineCount++;
            }
        }

        public void StopWithMoreContent()
        {
            HasMore = true;
            Stop = true;
        }

        public void SaveLastCompleteLine(BufferedTextReader input) =>
            LastCompleteLine = new PageCheckpoint(Content.Length, GetContinuation(input));

        public FileReadContinuation GetContinuation(BufferedTextReader input) =>
            position with
            {
                CharacterOffset = input.CharacterOffset,
                ByteOffset = input.ByteOffset,
                LineNumber = LineNumber,
                LineHasContent = LineHasContent,
            };
    }

    private readonly record struct PageCheckpoint(int Length, FileReadContinuation Position);

    private sealed class BufferedTextReader
    {
        private const int BufferSize = 8 * 1024;
        private readonly StreamReader _reader;
        private readonly Stream _stream;
        private readonly Encoding _encoding;
        private readonly long _fileLength;
        private readonly char[] _buffer = new char[BufferSize];
        private byte[]? _utf8SourceWindow;
        private char[]? _utf8OutputBuffer;
        private long _utf8WindowOffset;
        private int _utf8WindowLength;
        private int _start;
        private int _available;
        private int _length;
        private bool _endOfStream;

        public long CharacterOffset { get; private set; }
        public long ByteOffset { get; private set; }

        public BufferedTextReader(StreamReader reader, FileReadContinuation position)
        {
            _reader = reader;
            _stream = reader.BaseStream;
            _encoding = reader.CurrentEncoding;
            _fileLength = _stream.Length;
            CharacterOffset = position.CharacterOffset;
            ByteOffset = position.ByteOffset;
            if (CharacterOffset > 0)
            {
                _stream.Position = ByteOffset;
            }
        }

        public async ValueTask<ReadOnlyMemory<char>> ReadChunkAsync(CancellationToken cancellationToken)
        {
            if (_start == _available && !_endOfStream)
            {
                var carry = _length - _available;
                if (carry > 0)
                {
                    _buffer[0] = _buffer[_available];
                }

                _start = 0;
                var read = await _reader
                    .ReadBlockAsync(_buffer.AsMemory(carry), cancellationToken)
                    .ConfigureAwait(false);
                _length = carry + read;
                _endOfStream = _length < _buffer.Length;
                _available = _length;
                if (!_endOfStream && (_buffer[^1] == '\r' || char.IsHighSurrogate(_buffer[^1])))
                {
                    _available--;
                }
            }

            return _buffer.AsMemory(_start, _available - _start);
        }

        public async ValueTask ConsumeAsync(int count, CancellationToken cancellationToken)
        {
            var consumed = _buffer.AsMemory(_start, count);
            var byteCount = await CountSourceBytesAsync(consumed, cancellationToken).ConfigureAwait(false);
            ByteOffset += Math.Min(byteCount, _fileLength - ByteOffset);
            CharacterOffset += count;
            _start += count;
        }

        private ValueTask<int> CountSourceBytesAsync(
            ReadOnlyMemory<char> consumed,
            CancellationToken cancellationToken
        ) =>
            _encoding.CodePage == Encoding.UTF8.CodePage && consumed.Span.Contains('\uFFFD')
                ? CountUtf8SourceBytesAsync(consumed, cancellationToken)
                : ValueTask.FromResult(_encoding.GetByteCount(consumed.Span));

        private async ValueTask<int> CountUtf8SourceBytesAsync(
            ReadOnlyMemory<char> consumed,
            CancellationToken cancellationToken
        )
        {
            await EnsureUtf8WindowAsync(consumed.Length, cancellationToken).ConfigureAwait(false);
            var source = _utf8SourceWindow!;
            var output = _utf8OutputBuffer!;
            var start = checked((int)(ByteOffset - _utf8WindowOffset));
            var sourceLength = _utf8WindowLength - start;
            var isFinalBlock = _utf8WindowOffset + _utf8WindowLength >= _fileLength;

            Utf8.ToUtf16(
                source.AsSpan(start, sourceLength),
                output.AsSpan(0, consumed.Length),
                out var bytesRead,
                out var charsWritten,
                replaceInvalidSequences: true,
                isFinalBlock
            );
            if (charsWritten != consumed.Length || !output.AsSpan(0, charsWritten).SequenceEqual(consumed.Span))
            {
                throw InvalidContinuation();
            }

            return bytesRead;
        }

        private async Task EnsureUtf8WindowAsync(int characterCount, CancellationToken cancellationToken)
        {
            _utf8SourceWindow ??= new byte[BufferSize * Utf8LookaheadBytes + Utf8LookaheadBytes];
            _utf8OutputBuffer ??= new char[BufferSize];
            var requiredByteCount = checked(characterCount * Utf8LookaheadBytes + Utf8LookaheadBytes);
            var availableBytes = _fileLength - ByteOffset;
            if (availableBytes < 0)
            {
                throw InvalidContinuation();
            }

            var cachedOffset = ByteOffset - _utf8WindowOffset;
            if (
                cachedOffset >= 0
                && cachedOffset <= _utf8WindowLength
                && _utf8WindowLength - cachedOffset >= Math.Min(requiredByteCount, availableBytes)
            )
            {
                return;
            }

            var originalPosition = _stream.Position;
            try
            {
                _stream.Position = ByteOffset;
                var length = (int)Math.Min(availableBytes, Math.Max(BufferSize, requiredByteCount));
                _utf8WindowLength = await _stream
                    .ReadAtLeastAsync(
                        _utf8SourceWindow.AsMemory(0, length),
                        length,
                        throwOnEndOfStream: false,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                _utf8WindowOffset = ByteOffset;
            }
            finally
            {
                _stream.Position = originalPosition;
            }
        }
    }
}
