using Agw.Agents.Execution.Agents.History;
using Agw.Projects.Application.History;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.CodexSdk.AppServer;
using OpenAI.CodexSdk.AppServer.Protocol;

namespace Agw.Agents.Tests;

public sealed class CodexHistoryAdapterTests : IDisposable
{
    private const string ThreadId = "thread-1";
    private const string TurnId = "turn-1";
    private readonly IDisposable _userScope = HistoryTestFixture.EnterUser();

    public void Dispose() => _userScope.Dispose();

    [Fact]
    public async Task RecordAsync_CodexItemsAndContentIndexes_PreserveStreamingBoundaries()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new CodexMessageAdapter());
        var token = TestContext.Current.CancellationToken;

        await recording.RecordAsync(Update("message-1", 0, new TextContent("First ")), token);
        await recording.RecordAsync(Update("message-1", 0, new TextContent("answer")), token);
        await recording.RecordAsync(Update("message-2", 0, new TextContent("Second answer")), token);
        await recording.RecordAsync(Update("reasoning-1", 0, new TextReasoningContent("summary")), token);
        await recording.RecordAsync(Update("reasoning-1", 1, new TextReasoningContent("analysis")), token);
        await recording.FinishAsync(completed: true, token);

        var messages = await fixture.ReadMessagesAsync();
        var first = Assert.Single(messages, message => ItemId(message) == "message-1");
        var second = Assert.Single(messages, message => ItemId(message) == "message-2");
        var reasoning = Assert.Single(messages, message => ItemId(message) == "reasoning-1");

        Assert.NotEqual(first.MessageId, second.MessageId);
        Assert.Equal("First answer", first.Text);
        Assert.Equal("Second answer", second.Text);
        Assert.Collection(
            reasoning.Contents.OfType<TextReasoningContent>(),
            content =>
            {
                Assert.Equal("summary", content.Text);
                Assert.Equal("0", content.AdditionalProperties!["contentIndex"]?.ToString());
            },
            content =>
            {
                Assert.Equal("analysis", content.Text);
                Assert.Equal("1", content.AdditionalProperties!["contentIndex"]?.ToString());
            }
        );
        Assert.Equal(
            2,
            reasoning
                .Contents.OfType<TextReasoningContent>()
                .Select(content => content.AdditionalProperties!["blockId"]?.ToString())
                .Distinct(StringComparer.Ordinal)
                .Count()
        );
    }

    [Fact]
    public async Task RecordAsync_CodexReasoningKindsAtSameIndex_PreserveSeparateOrderedBlocks()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new CodexMessageAdapter());
        var token = TestContext.Current.CancellationToken;

        await recording.RecordAsync(
            Update("reasoning-1", 0, Reasoning("Summary ", "reasoning-1", 0, "summary")),
            token
        );
        await recording.RecordAsync(
            Update("reasoning-1", 0, Reasoning("complete", "reasoning-1", 0, "summary")),
            token
        );
        await recording.RecordAsync(
            Update("reasoning-1", 0, Reasoning("Analysis ", "reasoning-1", 0, "content")),
            token
        );
        await recording.RecordAsync(
            Update("reasoning-1", 0, Reasoning("complete", "reasoning-1", 0, "content")),
            token
        );
        await recording.FinishAsync(completed: true, token);

        var message = Assert.Single(await fixture.ReadMessagesAsync(), message => ItemId(message) == "reasoning-1");
        Assert.Collection(
            message.Contents.OfType<TextReasoningContent>(),
            summary =>
            {
                Assert.Equal("Summary complete", summary.Text);
                Assert.Equal("0", summary.AdditionalProperties!["contentIndex"]?.ToString());
                Assert.Equal("summary", summary.AdditionalProperties["reasoningKind"]?.ToString());
                Assert.Equal("block:summary:0", summary.AdditionalProperties["blockId"]?.ToString());
            },
            content =>
            {
                Assert.Equal("Analysis complete", content.Text);
                Assert.Equal("0", content.AdditionalProperties!["contentIndex"]?.ToString());
                Assert.Equal("content", content.AdditionalProperties["reasoningKind"]?.ToString());
                Assert.Equal("block:content:0", content.AdditionalProperties["blockId"]?.ToString());
            }
        );
    }

    [Fact]
    public async Task RecordAsync_CodexToolMediaAndRepeatedPathImages_PreservesEachItem()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new CodexMessageAdapter());
        var token = TestContext.Current.CancellationToken;
        var firstImage = ReadImageFixture("image-view-1.png");
        var secondImage = ReadImageFixture("image-view-2.png");

        var toolUpdate = Update(
            "mcp-item",
            null,
            new FunctionCallContent("call-1", "read_image", new Dictionary<string, object?>())
            {
                AdditionalProperties = ContentMetadata("mcp-item", null),
            },
            new FunctionResultContent("call-1", "Image inspected")
            {
                AdditionalProperties = ContentMetadata("mcp-item", null),
            },
            Image("mcp-item", 1, firstImage, "same-path.png")
        );
        await recording.RecordAsync(toolUpdate, token);
        await recording.RecordAsync(Update("image-1", 0, Image("image-1", 0, firstImage, "same-path.png")), token);
        await recording.RecordAsync(Update("image-2", 0, Image("image-2", 0, secondImage, "same-path.png")), token);
        await recording.FinishAsync(completed: true, token);

        var messages = await fixture.ReadMessagesAsync();
        var toolMessage = Assert.Single(messages, message => ItemId(message) == "mcp-item");
        Assert.Collection(
            toolMessage.Contents,
            content => Assert.IsType<FunctionCallContent>(content),
            content => Assert.IsType<FunctionResultContent>(content),
            content => Assert.IsType<DataContent>(content)
        );

        var images = messages
            .Where(message => ItemId(message) is "image-1" or "image-2")
            .ToDictionary(
                message => ItemId(message)!,
                message => Assert.IsType<DataContent>(Assert.Single(message.Contents))
            );
        Assert.Equal(2, images.Count);
        Assert.Equal(firstImage, images["image-1"].Data.ToArray());
        Assert.Equal(secondImage, images["image-2"].Data.ToArray());
        foreach (var (itemId, image) in images)
        {
            Assert.Equal("image/png", image.MediaType);
            Assert.StartsWith("data:image/png;base64,", image.Uri.ToString(), StringComparison.Ordinal);
            Assert.Equal(itemId, image.AdditionalProperties!["itemId"]?.ToString());
            Assert.Equal("0", image.AdditionalProperties["contentIndex"]?.ToString());
            Assert.Equal("same-path.png", image.Name);
        }
    }

    [Fact]
    public async Task RecordAsync_CodexImageViewNotifications_PersistsImagePathByExactIdentity()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new CodexMessageAdapter());
        var token = TestContext.Current.CancellationToken;
        var imageBytes = ReadImageFixture("image-view-1.png");

        var contentNotificationImage = Image("stream-content", 0, imageBytes, "content.png");
        contentNotificationImage.RawRepresentation = CreateCompletedImageView(
            "stream-content",
            "/workspace/content.png"
        );
        var updateNotificationImage = Image("stream-update", 1, imageBytes, "update.png");
        var unmatchedStreamImage = Image("stream-unmatched", 2, imageBytes, "unmatched.png");
        var streamUpdate = Update(
            "stream-content",
            null,
            contentNotificationImage,
            updateNotificationImage,
            unmatchedStreamImage
        );
        streamUpdate.RawRepresentation = new AppServerNotification[]
        {
            CreateCompletedImageView("stream-update", "/workspace/update.png"),
            CreateCompletedImageView("stream-content", "/workspace/wrong-turn.png", ThreadId, "turn-other"),
            CreateCompletedImageView("stream-content", "/workspace/wrong-thread.png", "thread-other", TurnId),
            CreateCompletedImageView("different-item", "/workspace/wrong-item.png"),
        };
        await recording.RecordAsync(streamUpdate, token);

        var completeNotificationImage = Image("complete-source", 0, imageBytes, "complete.png");
        var unmatchedCompleteImage = Image("complete-unmatched", 1, imageBytes, "complete-unmatched.png");
        var completeMessage = new ChatMessage(
            ChatRole.Assistant,
            [completeNotificationImage, unmatchedCompleteImage]
        )
        {
            MessageId = "complete-source",
            AdditionalProperties = ContentMetadata("complete-source", null),
            RawRepresentation = new AppServerNotification[]
            {
                CreateCompletedImageView("complete-source", "/workspace/complete.png"),
                CreateCompletedImageView("complete-source", "/workspace/wrong-turn.png", ThreadId, "turn-other"),
                CreateCompletedImageView("complete-source", "/workspace/wrong-thread.png", "thread-other", TurnId),
                CreateCompletedImageView("different-item", "/workspace/wrong-item.png"),
            },
        };
        await recording.CalibrateAsync([completeMessage], null, token);
        await recording.FinishAsync(completed: true, token);

        var messages = await fixture.ReadMessagesAsync();
        var streamedImages = Assert.Single(messages, message => ItemId(message) == "stream-content")
            .Contents.OfType<DataContent>()
            .ToDictionary(image => image.AdditionalProperties!["itemId"]!.ToString()!);
        Assert.Equal(
            "/workspace/content.png",
            streamedImages["stream-content"].AdditionalProperties!["imagePath"]?.ToString()
        );
        Assert.Equal(
            "/workspace/update.png",
            streamedImages["stream-update"].AdditionalProperties!["imagePath"]?.ToString()
        );
        Assert.False(streamedImages["stream-unmatched"].AdditionalProperties?.ContainsKey("imagePath"));

        var completeImages = messages
            .Where(message => ItemId(message) is "complete-source" or "complete-unmatched")
            .ToDictionary(
                message => ItemId(message)!,
                message => Assert.IsType<DataContent>(Assert.Single(message.Contents))
            );
        Assert.Equal(
            "/workspace/complete.png",
            completeImages["complete-source"].AdditionalProperties!["imagePath"]?.ToString()
        );
        Assert.False(completeImages["complete-unmatched"].AdditionalProperties?.ContainsKey("imagePath"));
    }

    [Fact]
    public async Task RecordAsync_CodexFinalAnswerAfterTextDelta_PersistsOneStructuredResult()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new CodexMessageAdapter(), structuredResult: true);
        var token = TestContext.Current.CancellationToken;
        var finalText = "{\"answer\":true}";
        await recording.RecordAsync(Update("final-1", 0, new TextContent(finalText)), token);
        await recording.RecordAsync(
            new AgentResponseUpdate
            {
                RawRepresentation = CreateCompletedMessage("commentary-1", "commentary", "Progress"),
            },
            token
        );
        await recording.RecordAsync(
            new AgentResponseUpdate
            {
                RawRepresentation = CreateCompletedMessage("final-1", "final_answer", finalText),
            },
            token
        );
        await recording.RecordAsync(
            new AgentResponseUpdate
            {
                RawRepresentation = CreateCompletedMessage("final-1", "final_answer", finalText),
            },
            token
        );
        await recording.FinishAsync(completed: true, token);

        var rows = await fixture.ReadAsync();
        var messages = rows.Select(row => row.ToChatMessage()!).ToList();
        var ordinary = Assert.Single(
            messages,
            message =>
                ItemId(message) == "final-1"
                && message.AdditionalProperties?.GetValueOrDefault("type")?.ToString() != "result"
        );
        var result = Assert.Single(
            messages,
            message => message.AdditionalProperties?.GetValueOrDefault("type")?.ToString() == "result"
        );
        Assert.Equal(finalText, ordinary.Text);
        Assert.Equal(finalText, result.Text);
        Assert.Equal("json", result.AdditionalProperties!["resultFormat"]?.ToString());
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.Equal(ordinary.MessageId, result.AdditionalProperties["resultSourceMessageId"]?.ToString());
    }

    [Fact]
    public async Task CalibrateAsync_CodexReasoningIndexesWithRepeatedChunks_PreservesTwoBlocks()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new CodexMessageAdapter());
        var token = TestContext.Current.CancellationToken;
        var response = new ChatMessage(
            ChatRole.Assistant,
            [
                Reasoning("Summary ", "reasoning-1", 0),
                Reasoning("complete", "reasoning-1", 0),
                Reasoning("Analysis ", "reasoning-1", 1),
                Reasoning("complete", "reasoning-1", 1),
            ]
        );

        await recording.CalibrateAsync([response], null, token);
        await recording.FinishAsync(completed: true, token);

        var message = Assert.Single(await fixture.ReadMessagesAsync(), message => ItemId(message) == "reasoning-1");
        Assert.Collection(
            message.Contents.OfType<TextReasoningContent>(),
            summary =>
            {
                Assert.Equal("Summary complete", summary.Text);
                Assert.Equal("0", summary.AdditionalProperties!["contentIndex"]?.ToString());
                Assert.Equal("block:0", summary.AdditionalProperties["blockId"]?.ToString());
            },
            analysis =>
            {
                Assert.Equal("Analysis complete", analysis.Text);
                Assert.Equal("1", analysis.AdditionalProperties!["contentIndex"]?.ToString());
                Assert.Equal("block:1", analysis.AdditionalProperties["blockId"]?.ToString());
            }
        );
    }

    [Fact]
    public async Task CalibrateAsync_CodexReasoningKindsAtSameIndex_PreservesSeparateOrderedBlocks()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new CodexMessageAdapter());
        var token = TestContext.Current.CancellationToken;
        var response = new ChatMessage(
            ChatRole.Assistant,
            [
                Reasoning("Summary ", "reasoning-1", 0, "summary"),
                Reasoning("complete", "reasoning-1", 0, "summary"),
                Reasoning("Analysis ", "reasoning-1", 0, "content"),
                Reasoning("complete", "reasoning-1", 0, "content"),
            ]
        );

        await recording.CalibrateAsync([response], null, token);
        await recording.FinishAsync(completed: true, token);

        var message = Assert.Single(await fixture.ReadMessagesAsync(), message => ItemId(message) == "reasoning-1");
        Assert.Collection(
            message.Contents.OfType<TextReasoningContent>(),
            summary =>
            {
                Assert.Equal("Summary complete", summary.Text);
                Assert.Equal("0", summary.AdditionalProperties!["contentIndex"]?.ToString());
                Assert.Equal("summary", summary.AdditionalProperties["reasoningKind"]?.ToString());
                Assert.Equal("block:summary:0", summary.AdditionalProperties["blockId"]?.ToString());
            },
            content =>
            {
                Assert.Equal("Analysis complete", content.Text);
                Assert.Equal("0", content.AdditionalProperties!["contentIndex"]?.ToString());
                Assert.Equal("content", content.AdditionalProperties["reasoningKind"]?.ToString());
                Assert.Equal("block:content:0", content.AdditionalProperties["blockId"]?.ToString());
            }
        );
    }

    [Fact]
    public async Task CalibrateAsync_CodexAggregateWithFinalItem_PreservesItemsAndResultMetadata()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync();
        var recording = fixture.CreateRecording(new CodexMessageAdapter(), structuredResult: true);
        var token = TestContext.Current.CancellationToken;
        var image1 = ReadImageFixture("image-view-1.png");
        var image2 = ReadImageFixture("image-view-2.png");
        var finalText = "{\"answer\":true}";
        var response = new ChatMessage(
            ChatRole.Assistant,
            [
                Text("Checking files", "progress-1", 0),
                new FunctionCallContent("call-1", "read_image", new Dictionary<string, object?>())
                {
                    AdditionalProperties = ContentMetadata("mcp-item", null),
                },
                new FunctionResultContent("call-1", "Image inspected")
                {
                    AdditionalProperties = ContentMetadata("mcp-item", null),
                },
                Image("mcp-item", 1, image1, "same-path.png"),
                Image("view-1", 0, image1, "same-path.png"),
                Image("view-2", 0, image2, "same-path.png"),
                Text("{\"answer\":", "final-1", 0),
                Text("true}", "final-1", 0),
            ]
        )
        {
            RawRepresentation = new AppServerNotification[]
            {
                CreateCompletedMessage("progress-1", "commentary", "Checking files"),
                CreateCompletedMessage("final-1", "final_answer", finalText),
            },
        };

        await recording.CalibrateAsync([response], null, token);
        await recording.FinishAsync(completed: true, token);

        var messages = await fixture.ReadMessagesAsync();
        var result = Assert.Single(
            messages,
            message => message.AdditionalProperties?.GetValueOrDefault("type")?.ToString() == "result"
        );
        var final = Assert.Single(
            messages,
            message =>
                ItemId(message) == "final-1"
                && message.AdditionalProperties?.GetValueOrDefault("type")?.ToString() != "result"
        );
        Assert.Equal(finalText, final.Text);
        Assert.Equal(finalText, result.Text);
        Assert.Equal("json", result.AdditionalProperties!["resultFormat"]?.ToString());
        Assert.Equal(final.MessageId, result.AdditionalProperties["resultSourceMessageId"]?.ToString());
        Assert.Single(messages, message => ItemId(message) == "progress-1");

        var toolMessage = Assert.Single(messages, message => ItemId(message) == "mcp-item");
        Assert.Collection(
            toolMessage.Contents,
            content => Assert.IsType<FunctionCallContent>(content),
            content => Assert.IsType<FunctionResultContent>(content),
            content => Assert.IsType<DataContent>(content)
        );

        var imageMessages = messages
            .Where(message => ItemId(message) is "view-1" or "view-2")
            .ToDictionary(
                message => ItemId(message)!,
                message => Assert.IsType<DataContent>(Assert.Single(message.Contents))
            );
        Assert.Equal(image1, imageMessages["view-1"].Data.ToArray());
        Assert.Equal(image2, imageMessages["view-2"].Data.ToArray());
        foreach (var (itemId, image) in imageMessages)
        {
            Assert.Equal("image/png", image.MediaType);
            Assert.StartsWith("data:image/png;base64,", image.Uri.ToString(), StringComparison.Ordinal);
            Assert.Equal(itemId, image.AdditionalProperties!["itemId"]?.ToString());
            Assert.Equal("0", image.AdditionalProperties["contentIndex"]?.ToString());
            Assert.Equal("same-path.png", image.Name);
        }
    }

    private static AgentResponseUpdate Update(string itemId, int? contentIndex, params AIContent[] contents)
    {
        var metadata = ContentMetadata(itemId, contentIndex);
        foreach (var content in contents)
        {
            content.AdditionalProperties ??= [];
            foreach (var (key, value) in metadata)
                if (!content.AdditionalProperties.ContainsKey(key))
                    content.AdditionalProperties[key] = value;
        }

        return new AgentResponseUpdate(ChatRole.Assistant, contents) { AdditionalProperties = metadata };
    }

    private static TextContent Text(string value, string itemId, int contentIndex) =>
        new(value) { AdditionalProperties = ContentMetadata(itemId, contentIndex) };

    private static TextReasoningContent Reasoning(
        string value,
        string itemId,
        int contentIndex,
        string? reasoningKind = null
    )
    {
        var properties = ContentMetadata(itemId, contentIndex);
        if (reasoningKind != null)
            properties["reasoningKind"] = reasoningKind;
        return new(value) { AdditionalProperties = properties };
    }

    private static DataContent Image(string itemId, int contentIndex, byte[] bytes, string name) =>
        new(bytes, "image/png") { Name = name, AdditionalProperties = ContentMetadata(itemId, contentIndex) };

    private static AdditionalPropertiesDictionary ContentMetadata(string? itemId, int? contentIndex)
    {
        var properties = new AdditionalPropertiesDictionary { ["threadId"] = ThreadId, ["turnId"] = TurnId };
        if (itemId != null)
            properties["itemId"] = itemId;
        if (contentIndex.HasValue)
            properties["contentIndex"] = contentIndex.Value;
        return properties;
    }

    private static string? ItemId(ChatMessage message) =>
        message.AdditionalProperties?.GetValueOrDefault("itemId")?.ToString();

    private static byte[] ReadImageFixture(string name) =>
        File.ReadAllBytes(
            Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "CodexAppServer", name)
            )
        );

    private static AppServerNotification CreateCompletedMessage(string itemId, string phase, string text) =>
        new(
            "item/completed",
            new ItemCompletedNotification
            {
                CompletedAtMs = 1,
                Item = new AgentMessageThreadItem
                {
                    Id = itemId,
                    Phase = CreatePhase(phase),
                    Text = text,
                },
                ThreadId = ThreadId,
                TurnId = TurnId,
            },
            ThreadId,
            TurnId
        );

    private static AppServerNotification CreateCompletedImageView(
        string itemId,
        string path,
        string threadId = ThreadId,
        string turnId = TurnId
    ) =>
        new(
            "item/completed",
            new ItemCompletedNotification
            {
                CompletedAtMs = 1,
                Item = new ImageViewThreadItem { Id = itemId, Path = path },
                ThreadId = threadId,
                TurnId = turnId,
            },
            threadId,
            turnId
        );

    private static MessagePhase? CreatePhase(string phase) =>
        phase switch
        {
            "final_answer" => new StringMessagePhase083A0BEF { Value = StringMessagePhaseValue083A0BEF.FinalAnswer },
            "commentary" => new StringMessagePhase { Value = StringMessagePhaseValue.Commentary },
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };
}
