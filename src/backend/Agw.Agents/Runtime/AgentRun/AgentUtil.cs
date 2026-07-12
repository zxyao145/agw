using Agw.Agents.Runtime.AgentRun.Dtos;
using Agw.Shared.AgwMsgVm;

using ClaudeCodeSdk.MAF;

namespace Agw.Agents.Runtime.AgentRun;

public class AgentUtil
{
    
    /// <summary>
    /// 处理 result message
    /// </summary>
    /// <param name="session"></param>
    /// <param name="agwMessage"></param>
    /// <returns></returns>
    public static AgwMessage PostAgwMessage(AgentExecSession session, AgwMessage agwMessage)
    {
        if (AgentUtil.IsResult(session, agwMessage))
        {
            agwMessage = agwMessage with
            {
                Author = Constants.DefaultAgentAuthor
            };
        }

        return agwMessage;
    }
    
    
    public static bool IsResult(AgentExecSession session, AgwMessage agwMessage)
    {
        if (session.Agent is ClaudeCodeAIAgent)
        {
            if (agwMessage.AdditionalProperties != null
                && agwMessage.AdditionalProperties.TryGetValue("type", out object? type))
            {
                string? typeValue = (string?)type;
                if (typeValue == "result")
                {
                    return true;
                }
            }
        }
        
        return false;
    }
}
