using Agw.Skills.Application.Content;
using Microsoft.Extensions.Hosting;

namespace Agw.Skills.Infrastructure.Content;

public sealed class SkillContentSweepService : BackgroundService
{
    private readonly ISkillContentRetirement _contentRetirement;

    public SkillContentSweepService(ISkillContentRetirement contentRetirement)
    {
        _contentRetirement = contentRetirement;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _contentRetirement.SweepAsync(stoppingToken);
}
