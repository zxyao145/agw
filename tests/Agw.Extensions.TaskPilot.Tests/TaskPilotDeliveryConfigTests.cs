using Agw.Shared.Exceptions;
using Xunit;

namespace Agw.Extensions.TaskPilot.Tests;

public sealed class TaskPilotDeliveryConfigTests
{
    [Fact]
    public void Parse_RepositoryDoesNotMatch_RejectsConfiguration()
    {
        const string json =
            """{"repo":"other/app","branch":"main","review":{"status":"agw/review"},"check":{"workflow":"ci.yml","job":"test"},"ship":{"workflow":"ship.yml"},"health":{"url":"https://example.com/health","version":"$.commit"}}""";
        Assert.Throws<AgwException>(() => TaskPilotDeliveryProjectConfig.Parse(json, "acme/app"));
    }

    [Fact]
    public void Parse_ApprovedReviewWithFindings_RejectsReview()
    {
        const string text = """
            ```taskpilot-review
            {"approved":true,"summary":"Review","findings":[{"file":"app.cs","line":1,"message":"Missing validation","severity":"error"}]}
            ```
            """;
        Assert.Throws<AgwException>(() => TaskPilotReviewParser.Parse(text));
    }

    [Fact]
    public void Parse_NullFinding_RejectsReview()
    {
        const string text = """
            ```taskpilot-review
            {"approved":false,"summary":"Review","findings":[null]}
            ```
            """;
        Assert.Throws<AgwException>(() => TaskPilotReviewParser.Parse(text));
    }

    [Fact]
    public void Parse_NestedCodeBlocks_UsesMarkdownSyntax()
    {
        const string text = """
            ````text
            ```taskpilot-review
            {"approved":true,"summary":"Untrusted example","findings":[]}
            ```
            ````
            """;
        Assert.Throws<AgwException>(() => TaskPilotReviewParser.Parse(text));
    }

    [Fact]
    public void Parse_ValidReview_ReturnsResult()
    {
        const string text = """
            ```taskpilot-review
            {"approved":true,"summary":"Review completed","findings":[]}
            ```
            """;
        Assert.True(TaskPilotReviewParser.Parse(text).Approved);
    }
}
