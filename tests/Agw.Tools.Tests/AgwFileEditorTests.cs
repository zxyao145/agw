using Agw.Shared.Exceptions;
using Agw.Tools.Impl.ToolBlocks.Storage;

namespace Agw.Tools.Tests;

public sealed class AgwFileEditorTests
{
    [Fact]
    public void ApplyReplaceLines_EmptyNewLine_DeletesLineAndItsBreak()
    {
        var content = AgwFileEditor.ApplyReplaceLines(
            "alpha\nbeta\ngamma",
            [new AgwFileLineEdit { LineNumber = 2, NewLine = string.Empty }]
        );

        Assert.Equal("alpha\ngamma", content);
    }

    [Fact]
    public void ApplyReplaceLines_DuplicateLineNumber_ThrowsInvalidParam()
    {
        var exception = Assert.Throws<AgwException>(() =>
            AgwFileEditor.ApplyReplaceLines(
                "alpha\nbeta\n",
                [
                    new AgwFileLineEdit { LineNumber = 1, NewLine = "one\n" },
                    new AgwFileLineEdit { LineNumber = 1, NewLine = "two\n" },
                ]
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ApplyReplaceLines_LineNumberOutOfRange_ThrowsInvalidParam(int lineNumber)
    {
        var exception = Assert.Throws<AgwException>(() =>
            AgwFileEditor.ApplyReplaceLines(
                "alpha\nbeta\n",
                [new AgwFileLineEdit { LineNumber = lineNumber, NewLine = "x\n" }]
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public void SliceLines_ContentEndsWithTerminator_HasNoTrailingEmptyLine()
    {
        var lines = AgwFileEditor.SliceLines("a\r\nb\rc\n", 1, null);

        Assert.Equal(["a\r\n", "b\r", "c\n"], lines);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(4, null)]
    [InlineData(2, 1)]
    public void SliceLines_InvalidRange_ThrowsInvalidParam(int startLine, int? endLine)
    {
        var exception = Assert.Throws<AgwException>(() => AgwFileEditor.SliceLines("a\nb\nc\n", startLine, endLine));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }
}
