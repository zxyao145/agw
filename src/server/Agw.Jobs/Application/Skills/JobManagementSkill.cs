using System.ComponentModel;
using Microsoft.Agents.AI;

namespace Agw.Jobs.Application.Skills;

#pragma warning disable MAAI001

internal sealed class JobManagementSkill : AgentClassSkill<JobManagementSkill>
{
    public override AgentSkillFrontmatter Frontmatter { get; } =
        new(
            JobManagementSkillRegistration.SkillName,
            "Manage scheduled jobs in the current project. Use when asked to list, inspect, create, modify, enable, disable, or delete jobs."
        );

    protected override string Instructions =>
        """
            Use this skill only to manage jobs in the current project.

            - Use agw_job_list to discover jobs and agw_job_get before changing or deleting a job.
            - Read job-trigger-reference before creating a job or changing its schedule.
            - agw_job_create and agw_job_update require an interactive user context.
            - agw_job_update has patch semantics. Omitted values remain unchanged.
            - To clear a prompt, set clearPrompt to true. Do not send prompt and clearPrompt together.
            - When changing the agent target, provide both agentType and agentId.
            - Before deleting, show the job details and ask the user to confirm the exact job ID.
            - Call agw_job_delete only after confirmation, passing the same job ID in confirmation.
            - Report the returned job ID and nextRunTime. Never claim a write succeeded without a successful tool result.
            """;

    [AgentSkillResource("job-trigger-reference")]
    [Description("Valid trigger types and values for scheduled jobs.")]
    public string JobTriggerReference =>
        """
            # Job trigger reference

            All schedules are evaluated in UTC.

            - Once: an RFC 3339 timestamp with `Z` or an explicit offset, for example `2026-08-01T09:00:00Z`.
            - Interval: a positive .NET TimeSpan value, for example `00:15:00` for fifteen minutes.
            - Cron: a standard five-field cron expression interpreted in UTC, for example `0 9 * * 1-5`.

            AgentType values are `Agent` and `Agentflow`.
            """;
}

#pragma warning restore MAAI001
