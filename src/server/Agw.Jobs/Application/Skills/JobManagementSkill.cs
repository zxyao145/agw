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
            - When changing the target, provide both targetKind and agentId.
            - Before deleting, show the job details and ask the user to confirm the exact job ID.
            - Call agw_job_delete only after confirmation, passing the same job ID in confirmation.
            - Report the returned job ID and dueAt. Never claim a write succeeded without a successful tool result.
            """;

    [AgentSkillResource("job-trigger-reference")]
    [Description("Valid schedule kinds and values for scheduled jobs.")]
    public string JobTriggerReference =>
        """
            # Job schedule reference

            All schedules are evaluated in UTC.

            - Once: set kind to `Once` and `at` to an RFC 3339 timestamp, for example `2026-08-01T09:00:00Z`. A time that is not in the future runs once immediately.
            - Interval: set kind to `Interval` and `interval` to a positive ISO 8601 duration, for example `PT15M`.
            - Cron: set kind to `Cron` and `cron` to a standard five-field cron expression, for example `0 9 * * 1-5`.

            targetKind values are `Agent` and `Agentflow`.
            """;
}

#pragma warning restore MAAI001
