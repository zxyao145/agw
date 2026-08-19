using Agw.Agents.Execution.Commands.Setting;
using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Turns;

internal sealed class PermissionModeState
{
    private readonly object _lock = new();
    private readonly HashSet<AgentSession> _sessions = new(ReferenceEqualityComparer.Instance);
    private int _value;

    public PermissionModeState(PermissionMode? permissionMode)
    {
        _value = Encode(permissionMode);
    }

    public PermissionMode? Current => Decode(Volatile.Read(ref _value));

    public void Register(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_lock)
        {
            _sessions.Add(session);
            ToolApprovalPermissionState.Apply(session, Decode(_value));
        }
    }

    public void Set(PermissionMode permissionMode)
    {
        lock (_lock)
        {
            foreach (var session in _sessions)
            {
                ToolApprovalPermissionState.Apply(session, permissionMode);
            }

            Volatile.Write(ref _value, Encode(permissionMode));
        }
    }

    private static int Encode(PermissionMode? permissionMode) =>
        permissionMode.HasValue ? (int)permissionMode.Value + 1 : 0;

    private static PermissionMode? Decode(int value) => value == 0 ? null : (PermissionMode)(value - 1);
}
