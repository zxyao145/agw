using System.Security.Cryptography;
using System.Text;

namespace Agw.Agents.Execution.HumanInteraction.Application;

internal static class InteractionIdentity
{
    public static string ForProviderRequest(string nodeId, string providerRequestId) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{nodeId.Length}:{nodeId}{providerRequestId}"))
        )[..32];
}
