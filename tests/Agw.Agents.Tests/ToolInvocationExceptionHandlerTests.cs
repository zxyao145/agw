using System.Diagnostics;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Files.Exceptions;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class ToolInvocationExceptionHandlerTests
{
    [Fact]
    public void IsErrorResult_RecognizesObjectAndSerializedPayload()
    {
        var error = new ToolExecutionErrorResult(
            IsError: true,
            Code: ErrorCodes.ToolExecutionFailed.Code,
            Message: ErrorCodes.ToolExecutionFailed.Message
        );

        Assert.True(ToolInvocationExceptionHandler.IsErrorResult(error));
        Assert.True(ToolInvocationExceptionHandler.IsErrorResult(JsonSerializer.SerializeToElement(error)));
        Assert.False(ToolInvocationExceptionHandler.IsErrorResult("not an error result"));
    }

    [Fact]
    public async Task InvokeAsync_UnknownException_ReturnsSanitizedResultAndClientPayload()
    {
        // Arrange
        var function = CreateThrowingFunction(new InvalidOperationException("secret implementation detail"));
        var handler = CreateHandler();
        var context = CreateContext(function);

        // Act
        var result = await handler.InvokeAsync(context, CancellationToken.None);

        // Assert
        var error = Assert.IsType<ToolExecutionErrorResult>(result);
        Assert.True(error.IsError);
        Assert.Equal(ErrorCodes.ToolExecutionFailed.Code, error.Code);
        Assert.Equal(ErrorCodes.ToolExecutionFailed.Message, error.Message);
        Assert.DoesNotContain("secret implementation detail", JsonSerializer.Serialize(error));

        var message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", error)]).ToAiMessage();
        var content = Assert.IsType<AgwFunctionResultContent>(Assert.Single(message!.Contents));
        var payload = JsonDocument.Parse(content.Content!).RootElement;
        Assert.True(payload.GetProperty("isError").GetBoolean());
        Assert.Equal(ErrorCodes.ToolExecutionFailed.Code, payload.GetProperty("code").GetInt32());
        Assert.Equal(ErrorCodes.ToolExecutionFailed.Message, payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task InvokeAsync_Failure_RecordsToolErrorActivity()
    {
        // Arrange
        using var activity = new Activity("tool-invocation").Start();
        var handler = CreateHandler();
        var context = CreateContext(CreateThrowingFunction(new InvalidOperationException("failure")));

        // Act
        await handler.InvokeAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        var errorEvent = Assert.Single(activity.Events, item => item.Name == "agw.tool.error");
        Assert.Contains(errorEvent.Tags, tag => tag.Key == "agw.tool.name" && Equals(tag.Value, "failing_tool"));
        Assert.Contains(
            errorEvent.Tags,
            tag => tag.Key == "agw.tool.error.code" && Equals(tag.Value, ErrorCodes.ToolExecutionFailed.Code)
        );
    }

    [Fact]
    public async Task InvokeAsync_AgwException_ReturnsOriginalCodeAndMessage()
    {
        // Arrange
        var exception = new AgwException(ErrorCodes.InvalidUrl, "The supplied URL is invalid.");
        var handler = CreateHandler();
        var context = CreateContext(CreateThrowingFunction(exception));

        // Act
        var result = await handler.InvokeAsync(context, CancellationToken.None);

        // Assert
        var error = Assert.IsType<ToolExecutionErrorResult>(result);
        Assert.Equal(ErrorCodes.InvalidUrl.Code, error.Code);
        Assert.Equal(exception.Message, error.Message);
    }

    [Fact]
    public async Task InvokeAsync_AgwFilesException_ReturnsOriginalCodeAndMessage()
    {
        // Arrange
        var exception = new AgwFilesException(FilesErrorCode.PathOutsideRoot, "The path is outside the workspace.");
        var handler = CreateHandler();
        var context = CreateContext(CreateThrowingFunction(exception));

        // Act
        var result = await handler.InvokeAsync(context, CancellationToken.None);

        // Assert
        var error = Assert.IsType<ToolExecutionErrorResult>(result);
        Assert.Equal(exception.Code, error.Code);
        Assert.Equal(exception.Message, error.Message);
    }

    [Fact]
    public async Task InvokeAsync_CallerCancellation_PropagatesCancellation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invocationCount = 0;
        var function = AIFunctionFactory.Create(
            (Func<string>)(
                () =>
                {
                    invocationCount++;
                    return "should not run";
                }
            ),
            new AIFunctionFactoryOptions { Name = "cancelled_tool" }
        );
        var handler = CreateHandler();
        var context = CreateContext(function);

        // Act
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.InvokeAsync(context, cancellation.Token).AsTask()
        );

        // Assert
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task InvokeAsync_ToolCancellationWithoutCallerCancellation_ReturnsGenericError()
    {
        // Arrange
        var handler = CreateHandler();
        var context = CreateContext(CreateThrowingFunction(new OperationCanceledException()));

        // Act
        var result = await handler.InvokeAsync(context, CancellationToken.None);

        // Assert
        var error = Assert.IsType<ToolExecutionErrorResult>(result);
        Assert.Equal(ErrorCodes.ToolExecutionFailed.Code, error.Code);
        Assert.Equal(ErrorCodes.ToolExecutionFailed.Message, error.Message);
    }

    private static ToolInvocationExceptionHandler CreateHandler() =>
        new(NullLogger<ToolInvocationExceptionHandler>.Instance);

    private static FunctionInvocationContext CreateContext(AIFunction function) =>
        new()
        {
            Function = function,
            Arguments = new AIFunctionArguments(),
            CallContent = new FunctionCallContent("call-1", function.Name, new Dictionary<string, object?>()),
        };

    private static AIFunction CreateThrowingFunction(Exception exception) =>
        AIFunctionFactory.Create(
            (Func<string>)(() => throw exception),
            new AIFunctionFactoryOptions { Name = "failing_tool" }
        );
}
