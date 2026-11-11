using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Extensions.Contracts;

namespace Agw.Extensions.Feishu;

public static class FeishuCards
{
    public static JsonElement BindCard(string confirmationUrl) =>
        ToElement(
            new JsonObject
            {
                ["schema"] = "2.0",
                ["config"] = new JsonObject { ["update_multi"] = false },
                ["header"] = new JsonObject
                {
                    ["title"] = new JsonObject
                    {
                        ["tag"] = "plain_text",
                        ["content"] = "Link your Feishu account to Agw",
                    },
                },
                ["body"] = new JsonObject
                {
                    ["direction"] = "vertical",
                    ["elements"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["tag"] = "markdown",
                            ["content"] = "Open this link while signed in to Agw to finish linking your account.",
                        },
                        new JsonObject
                        {
                            ["tag"] = "button",
                            ["text"] = new JsonObject { ["tag"] = "plain_text", ["content"] = "Confirm in Agw" },
                            ["type"] = "primary",
                            ["behaviors"] = new JsonArray
                            {
                                new JsonObject { ["type"] = "open_url", ["default_url"] = confirmationUrl },
                            },
                        },
                    },
                },
            }
        );

    public static JsonElement NotificationCard(NotificationMessage message, string? link, bool interactive)
    {
        var elements = new JsonArray
        {
            new JsonObject { ["tag"] = "markdown", ["content"] = $"**{message.Title}**\n\n{message.Body}" },
        };
        if (
            interactive
            && message.Kind == ExtensionNotificationKind.Question
            && message.State == ExtensionNotificationState.Open
        )
        {
            for (int index = 0; index < message.Options.Count; index++)
            {
                NotificationOption option = message.Options[index];
                elements.Add(
                    new JsonObject
                    {
                        ["tag"] = "button",
                        ["text"] = new JsonObject { ["tag"] = "plain_text", ["content"] = option.Label },
                        ["type"] = message.Recommend == index ? "primary" : "default",
                        ["value"] = new JsonObject
                        {
                            ["notificationId"] = message.NotificationId.ToString("D"),
                            ["dispatchKey"] = message.DispatchKey,
                            ["option"] = index,
                        },
                    }
                );
            }
        }
        if (!string.IsNullOrWhiteSpace(link))
        {
            elements.Add(
                new JsonObject
                {
                    ["tag"] = "button",
                    ["text"] = new JsonObject { ["tag"] = "plain_text", ["content"] = "Open Agw" },
                    ["type"] = "default",
                    ["behaviors"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "open_url", ["default_url"] = link },
                    },
                }
            );
        }
        return ToElement(
            new JsonObject
            {
                ["schema"] = "2.0",
                ["config"] = new JsonObject { ["update_multi"] = true },
                ["header"] = new JsonObject
                {
                    ["title"] = new JsonObject { ["tag"] = "plain_text", ["content"] = message.Title },
                },
                ["body"] = new JsonObject { ["direction"] = "vertical", ["elements"] = elements },
            }
        );
    }

    public static JsonElement CallbackCard(string title, string message) =>
        ToElement(
            new JsonObject
            {
                ["schema"] = "2.0",
                ["config"] = new JsonObject { ["update_multi"] = true },
                ["header"] = new JsonObject
                {
                    ["title"] = new JsonObject { ["tag"] = "plain_text", ["content"] = title },
                },
                ["body"] = new JsonObject
                {
                    ["direction"] = "vertical",
                    ["elements"] = new JsonArray
                    {
                        new JsonObject { ["tag"] = "markdown", ["content"] = message },
                    },
                },
            }
        );

    public static JsonElement CallbackToast(string type, string content) =>
        ToElement(new JsonObject { ["type"] = type, ["content"] = content });

    private static JsonElement ToElement(JsonObject value)
    {
        using JsonDocument document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }
}
