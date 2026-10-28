using System.Security.Cryptography;
using System.Text.Json;
using Agw.Projects.Application;
using Agw.Shared.Data.Entities.Projects;

namespace Agw.Agents.Tests;

public sealed class CodexImageFlowIntegrationTests
{
    public static bool CanRunImageFlow =>
        CodexAppServerFixture.IsAvailable && CodexLiveTestOptions.Current is { RunImageFlow: true };

    [Fact(SkipUnless = nameof(CanRunImageFlow), Skip = "Set RunImageFlow in test-data/codex-live.json.")]
    public async Task RunAsync_CodexImageGenerationAndView_PersistsLiveImagesAcrossResume()
    {
        using var user = ProviderSessionExecutionHarness.EnterUser();
        var options = Assert.IsType<CodexLiveTestOptions>(CodexLiveTestOptions.Current);
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(
            EngineKind.Codex,
            codexOptions: options
        );

        var firstTurnId = await harness.StartTurnAsync(
            "Use the image generation tool to create exactly one PNG of a blue square with an orange circle. "
                + "Save it as codex-imageflow.png in the current workspace. Then use the image viewing tool to inspect that file "
                + "and briefly confirm what it shows.",
            stream: true
        );
        Assert.NotEmpty(await harness.WaitForImageContentsAsync(firstTurnId));
        var firstBinding = Assert.Single(await harness.ReadBindingsAsync());
        Assert.True(firstBinding.IsActive);
        Assert.Equal(firstBinding.ProviderSessionId, harness.Runtime.ProviderSession!.PersistedProviderSessionId);
        await harness.WaitIdleAsync();
        var firstTurn = await harness.ReadTurnAsync(firstTurnId);
        Assert.True(firstTurn.Status == ProjectConversationTurnStatus.Completed, string.Join("\n", firstTurn.Errors));

        var firstRuntime = harness.Runtime;
        var firstImages = ReadImages(harness.ReadMessages(firstTurnId));
        Assert.Contains(firstImages, image => image.ImageSource == "imageGeneration");
        Assert.Contains(firstImages, image => image.ImageSource == "imageView");

        await harness.RebuildCoordinatorAsync();
        Assert.True(firstRuntime.IsDisposed);

        var secondTurnId = await harness.StartTurnAsync(
            "Use the image viewing tool to inspect the existing codex-imageflow.png in the current workspace. "
                + "Keep the file unchanged and briefly confirm what it shows.",
            stream: true
        );
        Assert.NotEmpty(await harness.WaitForImageContentsAsync(secondTurnId));
        await harness.WaitIdleAsync();
        var secondTurn = await harness.ReadTurnAsync(secondTurnId);
        Assert.True(secondTurn.Status == ProjectConversationTurnStatus.Completed, string.Join("\n", secondTurn.Errors));
        var secondImages = ReadImages(harness.ReadMessages(secondTurnId));
        Assert.NotEmpty(secondImages);
        Assert.All(secondImages, image => Assert.Equal("imageView", image.ImageSource));

        var resumedBinding = Assert.Single(await harness.ReadBindingsAsync());
        Assert.Equal(firstBinding.ProviderSessionId, resumedBinding.ProviderSessionId);
        Assert.All(secondImages, image => Assert.Equal(firstBinding.ProviderSessionId, image.Identity.ThreadId));
        Assert.All(secondImages, image => Assert.Equal(secondTurnId.ToString("D"), image.AgwTurnId));

        var imagePath = Path.Combine(harness.WorkspacePath, "codex-imageflow.png");
        Assert.True(File.Exists(imagePath));
        Assert.Equal(imagePath, Assert.Single(Directory.GetFiles(harness.WorkspacePath, "*.png")));
        var fileHash = Hash(File.ReadAllBytes(imagePath));
        Assert.Contains(firstImages, image => image.MediaType == "image/png" && image.Sha256 == fileHash);
        Assert.Contains(secondImages, image => image.MediaType == "image/png" && image.Sha256 == fileHash);

        var liveImages = firstImages.Concat(secondImages).ToList();
        Assert.All(liveImages, image => Assert.Equal("image/png", image.MediaType));
        Assert.All(liveImages, image => Assert.False(string.IsNullOrWhiteSpace(image.Identity.ItemId)));
        Assert.All(liveImages, image => Assert.False(string.IsNullOrWhiteSpace(image.Identity.ContentIndex)));
        Assert.All(liveImages, image => Assert.False(string.IsNullOrWhiteSpace(image.Identity.TurnId)));
        Assert.All(liveImages, image => Assert.True(image.ImageSource is "imageGeneration" or "imageView"));
        Assert.All(firstImages, image => Assert.Equal(firstBinding.ProviderSessionId, image.Identity.ThreadId));
        Assert.All(firstImages, image => Assert.Equal(firstTurnId.ToString("D"), image.AgwTurnId));
        var firstNativeTurnId = Assert.Single(firstImages.Select(image => image.Identity.TurnId).Distinct());
        var secondNativeTurnId = Assert.Single(secondImages.Select(image => image.Identity.TurnId).Distinct());
        Assert.NotEqual(firstNativeTurnId, secondNativeTurnId);

        var savedImages = (await harness.ReadHistoryAsync())
            .SelectMany(TaskExecutionMapper.ToAiMessages)
            .SelectMany(message =>
                message.Contents.OfType<AgwDataContent>().Select(content => CreateSnapshot(message, content))
            )
            .ToList();
        Assert.All(savedImages, image => Assert.Equal("image/png", image.MediaType));
        AssertSnapshotsEqual(liveImages, savedImages);
    }

    private static List<ImageSnapshot> ReadImages(IEnumerable<AgwMessage> messages) =>
        messages
            .SelectMany(message =>
                message.Contents.OfType<AgwDataContent>().Select(content => CreateSnapshot(message, content))
            )
            .ToList();

    private static ImageSnapshot CreateSnapshot(AgwMessage message, AgwDataContent content)
    {
        var properties = Assert.IsType<Microsoft.Extensions.AI.AdditionalPropertiesDictionary>(
            content.AdditionalProperties
        );
        var messageProperties = Assert.IsType<Microsoft.Extensions.AI.AdditionalPropertiesDictionary>(
            message.AdditionalProperties
        );
        return new ImageSnapshot(
            new ImageIdentity(
                ReadMetadata(properties, "threadId"),
                ReadMetadata(properties, "turnId"),
                ReadMetadata(properties, "itemId"),
                ReadMetadata(properties, "contentIndex")
            ),
            ReadMetadata(messageProperties, "turnId"),
            ReadMetadata(properties, "imageSource"),
            content.MediaType,
            content.Name,
            NormalizeMetadata(properties),
            Hash(content.Data.Span)
        );
    }

    private static string ReadMetadata(Microsoft.Extensions.AI.AdditionalPropertiesDictionary properties, string key)
    {
        Assert.True(properties.TryGetValue(key, out var value), $"Image metadata '{key}' was missing.");
        return value is JsonElement element ? element.ToString() : value?.ToString() ?? string.Empty;
    }

    private static string NormalizeMetadata(Microsoft.Extensions.AI.AdditionalPropertiesDictionary properties) =>
        JsonSerializer.Serialize(
            properties
                .OrderBy(property => property.Key, StringComparer.Ordinal)
                .Select(property => new KeyValuePair<string, string>(
                    property.Key,
                    NormalizeMetadataValue(property.Value)
                ))
                .ToList()
        );

    private static string NormalizeMetadataValue(object? value) =>
        value is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(value);

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    private static void AssertSnapshotsEqual(
        IReadOnlyList<ImageSnapshot> liveImages,
        IReadOnlyList<ImageSnapshot> savedImages
    )
    {
        var liveByIdentity = GroupSnapshots(liveImages);
        var savedByIdentity = GroupSnapshots(savedImages);
        var liveKeys = OrderIdentities(liveByIdentity.Keys);
        var savedKeys = OrderIdentities(savedByIdentity.Keys);
        Assert.Equal(liveKeys, savedKeys);
        foreach (var identity in liveKeys)
        {
            Assert.Equal(liveByIdentity[identity], savedByIdentity[identity]);
        }
    }

    private static Dictionary<ImageIdentity, ImageSnapshot> GroupSnapshots(IEnumerable<ImageSnapshot> images) =>
        images
            .GroupBy(image => image.Identity)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var first = group.First();
                    Assert.All(group, image => Assert.Equal(first, image));
                    return first;
                }
            );

    private static ImageIdentity[] OrderIdentities(IEnumerable<ImageIdentity> identities) =>
        identities
            .OrderBy(identity => identity.ThreadId, StringComparer.Ordinal)
            .ThenBy(identity => identity.TurnId, StringComparer.Ordinal)
            .ThenBy(identity => identity.ItemId, StringComparer.Ordinal)
            .ThenBy(identity => identity.ContentIndex, StringComparer.Ordinal)
            .ToArray();

    private readonly record struct ImageIdentity(string ThreadId, string TurnId, string ItemId, string ContentIndex);

    private sealed record ImageSnapshot(
        ImageIdentity Identity,
        string AgwTurnId,
        string ImageSource,
        string MediaType,
        string? Name,
        string MetadataJson,
        string Sha256
    );
}
