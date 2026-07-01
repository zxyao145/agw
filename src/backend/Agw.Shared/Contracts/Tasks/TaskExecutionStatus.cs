namespace Agw.Shared.Contracts.Tasks;

public enum TaskExecutionStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Canceled = 4
}
