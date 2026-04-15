using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

using Agw.Shared.Exceptions;

namespace Agw.Shared.Tests;

public class AgwExceptionTests
{
    [Fact]
    public void Constructor_WithCodeAndMessage_UsesBadRequestStatusCode()
    {
        var exception = new AgwException(1001, "Validation failed.");

        Assert.Equal(1001, exception.Code);
        Assert.Equal("Validation failed.", exception.Message);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [Fact]
    public void Constructor_WithCodeMessageAndStatusCode_UsesProvidedStatusCode()
    {
        var exception = new AgwException(1002, "Resource was not found.", HttpStatusCode.NotFound);

        Assert.Equal(1002, exception.Code);
        Assert.Equal("Resource was not found.", exception.Message);
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public void Constructor_WithErrorCode_UsesErrorCodeValues()
    {
        var errorCode = new ErrorCode(2001, "Request conflicted.", HttpStatusCode.Conflict);

        var exception = new AgwException(errorCode);

        Assert.Equal(errorCode.Code, exception.Code);
        Assert.Equal(errorCode.Message, exception.Message);
        Assert.Equal(errorCode.StatusCode, exception.StatusCode);
    }

    [Fact]
    public void ErrorCodes_AllCodesAreSevenDigitsAndMatchHttpStatusPrefix()
    {
        var fields = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(ErrorCode))
            .ToList();

        Assert.NotEmpty(fields);

        foreach (var field in fields)
        {
            var errorCode = Assert.IsType<ErrorCode>(field.GetValue(null));

            Assert.InRange(errorCode.Code, 1_000_000, 9_999_999);
            Assert.Equal((int)errorCode.StatusCode, errorCode.Code / 10_000);
            Assert.InRange(errorCode.Code % 10_000, 1, 9_999);
        }
    }

    [Fact]
    public void BackendSource_OnlyThrowsAgwExceptionWithThrowNew()
    {
        var repoRoot = FindRepositoryRoot();
        var backendRoot = Path.Combine(repoRoot, "src", "backend");
        var throwPattern = new Regex(@"throw\s+new\s+(?<type>[A-Za-z0-9_.]+)", RegexOptions.Compiled);
        var violations = Directory
            .EnumerateFiles(backendRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => throwPattern
                .Matches(File.ReadAllText(file))
                .Select(match => new
                {
                    File = Path.GetRelativePath(repoRoot, file),
                    Type = match.Groups["type"].Value
                }))
            .Where(match => match.Type != "AgwException")
            .Select(match => $"{match.File}: {match.Type}")
            .Order()
            .ToList();

        Assert.Empty(violations);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Agw.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root.");
    }
}
