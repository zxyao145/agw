namespace Agw.Extensions.Feishu;

internal static class FeishuDomains
{
    public const string FeishuOpenDomain = "https://open.feishu.cn";

    public const string LarkOpenDomain = "https://open.larksuite.com";

    public const string DefaultOpenDomain = FeishuOpenDomain;

    public static bool IsSupportedOpenDomain(string value) => value is FeishuOpenDomain or LarkOpenDomain;
}
