using System.Text.Json.Serialization;

namespace Agw.Agents.Execution.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SettingCommand), nameof(SettingCommand))]
[JsonDerivedType(typeof(ExecCommand), nameof(ExecCommand))]
[JsonDerivedType(typeof(InterruptCommand), nameof(InterruptCommand))]
[JsonDerivedType(typeof(HumanResponseCommand), nameof(HumanResponseCommand))]
public abstract class AgentRunCommand;
