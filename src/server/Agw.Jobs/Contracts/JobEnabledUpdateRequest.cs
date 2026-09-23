namespace Agw.Jobs.Contracts;

public class JobEnabledUpdateRequest
{
    public Guid JobId { get; set; }
    public bool IsEnabled { get; set; }
}
