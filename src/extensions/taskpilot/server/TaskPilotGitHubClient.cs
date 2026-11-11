using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Shared.Exceptions;

namespace Agw.Extensions.TaskPilot;

internal sealed class TaskPilotGitHubClient
{
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private readonly IHttpClientFactory _http;

    public TaskPilotGitHubClient(IHttpClientFactory http)
    {
        _http = http;
    }

    public Task<JsonElement> GetPullAsync(string repo, int number, string authorization, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, RepoPath(repo) + $"/pulls/{number}", authorization, null, ct);

    public async Task<string> GetBranchShaAsync(string repo, string branch, string authorization, CancellationToken ct)
    {
        var result = await SendAsync(
            HttpMethod.Get,
            RepoPath(repo) + "/commits/" + Escape(branch),
            authorization,
            null,
            ct
        );
        return result.GetProperty("sha").GetString()!;
    }

    public async Task DisableAutoMergeAsync(string repo, JsonElement pull, string authorization, CancellationToken ct)
    {
        if (!pull.TryGetProperty("auto_merge", out var autoMerge) || autoMerge.ValueKind == JsonValueKind.Null)
            return;
        var result = await SendAsync(
            HttpMethod.Post,
            "graphql",
            authorization,
            new JsonObject
            {
                ["query"] =
                    "mutation($id:ID!){disablePullRequestAutoMerge(input:{pullRequestId:$id}){pullRequest{id}}}",
                ["variables"] = new JsonObject { ["id"] = pull.GetProperty("node_id").GetString() },
            },
            ct
        );
        if (result.TryGetProperty("errors", out _))
            throw new AgwException(ErrorCodes.InvalidParam, "GitHub did not disable automatic merging.");
        var current = await GetPullAsync(repo, pull.GetProperty("number").GetInt32(), authorization, ct);
        if (current.GetProperty("auto_merge").ValueKind != JsonValueKind.Null)
            throw new AgwException(ErrorCodes.InvalidParam, "GitHub automatic merging remains enabled.");
    }

    public Task<JsonElement> CompareAsync(
        string repo,
        string ancestor,
        string descendant,
        string authorization,
        CancellationToken ct
    ) =>
        SendAsync(
            HttpMethod.Get,
            RepoPath(repo) + $"/compare/{Escape(ancestor)}...{Escape(descendant)}",
            authorization,
            null,
            ct
        );

    public async Task<bool> ContainsAsync(
        string repo,
        string ancestor,
        string descendant,
        string authorization,
        CancellationToken ct
    )
    {
        var comparison = await CompareAsync(repo, ancestor, descendant, authorization, ct);
        return comparison.GetProperty("status").GetString() is "ahead" or "identical";
    }

    public async Task UpdateBranchAsync(
        string repo,
        int number,
        string head,
        string authorization,
        CancellationToken ct
    ) =>
        _ = await SendAsync(
            HttpMethod.Put,
            RepoPath(repo) + $"/pulls/{number}/update-branch",
            authorization,
            new JsonObject { ["expected_head_sha"] = head },
            ct
        );

    public async Task<string> ReadFileAsync(
        string repo,
        string path,
        string sha,
        string authorization,
        CancellationToken ct
    )
    {
        string escapedPath = string.Join('/', path.Split('/').Select(Escape));
        var file = await SendAsync(
            HttpMethod.Get,
            RepoPath(repo) + $"/contents/{escapedPath}?ref={Escape(sha)}",
            authorization,
            null,
            ct
        );
        if (file.GetProperty("type").GetString() != "file" || file.GetProperty("encoding").GetString() != "base64")
            throw new AgwException(ErrorCodes.InvalidParam, "GitHub returned an unsupported file representation.");
        byte[] bytes = Convert.FromBase64String(file.GetProperty("content").GetString()!);
        if (bytes.Length > MaximumResponseBytes)
            throw new AgwException(ErrorCodes.InvalidParam, "The review file exceeds the snapshot limit.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    public async Task<string> CreateSnapshotAsync(
        string repo,
        JsonElement pull,
        string baseSha,
        string headSha,
        string authorization,
        CancellationToken ct
    )
    {
        var comparison = await CompareAsync(repo, baseSha, headSha, authorization, ct);
        int number = pull.GetProperty("number").GetInt32();
        var snapshot = new StringBuilder();
        snapshot.AppendLine(
            new JsonObject
            {
                ["repository"] = repo,
                ["pullRequest"] = number,
                ["baseSha"] = baseSha,
                ["headSha"] = headSha,
            }.ToJsonString()
        );
        int fileCount = 0;
        for (int page = 1; ; page++)
        {
            var files = await SendAsync(
                HttpMethod.Get,
                RepoPath(repo) + $"/pulls/{number}/files?per_page=100&page={page}",
                authorization,
                null,
                ct
            );
            foreach (var file in files.EnumerateArray())
            {
                fileCount++;
                string filename = file.GetProperty("filename").GetString()!;
                snapshot.AppendLine(JsonSerializer.Serialize(file));
                if (file.GetProperty("status").GetString() != "removed")
                    snapshot.AppendLine(
                        await ReadFileAsync(
                            pull.GetProperty("head").GetProperty("repo").GetProperty("full_name").GetString()!,
                            filename,
                            headSha,
                            authorization,
                            ct
                        )
                    );
                if (snapshot.Length > 500_000)
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        "The pull request exceeds the review snapshot limit."
                    );
            }
            if (files.GetArrayLength() < 100)
                break;
            if (page >= 30)
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "GitHub did not return the complete pull request file list."
                );
        }
        if (fileCount != pull.GetProperty("changed_files").GetInt32())
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The pull request file list changed during snapshot creation."
            );
        snapshot.AppendLine("Comparison: " + comparison.GetProperty("status").GetString());
        var current = await GetPullAsync(repo, number, authorization, ct);
        if (current.GetProperty("head").GetProperty("sha").GetString() != headSha)
            throw new AgwException(ErrorCodes.InvalidParam, "The pull request changed during snapshot creation.");
        return snapshot.ToString();
    }

    public async Task SetStatusAsync(
        string repo,
        string sha,
        string context,
        string state,
        string description,
        string authorization,
        CancellationToken ct
    ) =>
        _ = await SendAsync(
            HttpMethod.Post,
            RepoPath(repo) + $"/statuses/{Escape(sha)}",
            authorization,
            new JsonObject
            {
                ["state"] = state,
                ["context"] = context,
                ["description"] = description.Length > 140 ? description[..140] : description,
            },
            ct
        );

    public async Task<bool> MergeAsync(
        string repo,
        int number,
        string reviewedSha,
        string authorization,
        CancellationToken ct
    )
    {
        try
        {
            using var response = await SendResponseAsync(
                HttpMethod.Put,
                RepoPath(repo) + $"/pulls/{number}/merge",
                authorization,
                new JsonObject { ["sha"] = reviewedSha },
                ct
            );
            if (response.StatusCode == HttpStatusCode.Conflict)
                return false;
            response.EnsureSuccessStatusCode();
            var result = await ReadJsonAsync(response, ct);
            if (!result.GetProperty("merged").GetBoolean())
                throw new AgwException(ErrorCodes.InvalidParam, "GitHub did not merge the reviewed commit.");
            return true;
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                || exception is OperationCanceledException && !ct.IsCancellationRequested
            )
        {
            var pull = await GetPullAsync(repo, number, authorization, ct);
            if (pull.GetProperty("merged").GetBoolean())
                return true;
            if (pull.GetProperty("head").GetProperty("sha").GetString() != reviewedSha)
                return false;
            throw;
        }
    }

    public async Task RemoveLabelAsync(
        string repo,
        int number,
        string label,
        string authorization,
        CancellationToken ct
    )
    {
        using var response = await SendResponseAsync(
            HttpMethod.Delete,
            RepoPath(repo) + $"/issues/{number}/labels/{Escape(label)}",
            authorization,
            null,
            ct
        );
        if (response.StatusCode != HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    public async Task<JsonElement?> FindWorkflowAsync(
        string repo,
        string workflow,
        string branch,
        string authorization,
        Func<JsonElement, Task<bool>> accept,
        CancellationToken ct
    )
    {
        for (int page = 1; page <= 10; page++)
        {
            var result = await SendAsync(
                HttpMethod.Get,
                RepoPath(repo)
                    + $"/actions/workflows/{Escape(workflow)}/runs?branch={Escape(branch)}&per_page=100&page={page}",
                authorization,
                null,
                ct
            );
            var runs = result.GetProperty("workflow_runs");
            foreach (var run in runs.EnumerateArray().OrderByDescending(run => run.GetProperty("id").GetInt64()))
                if (await accept(run))
                    return run.Clone();
            if (runs.GetArrayLength() < 100)
                return null;
        }
        throw new AgwException(ErrorCodes.InvalidParam, "The workflow search exceeds the supported run window.");
    }

    public async Task<string?> GetJobConclusionAsync(
        string repo,
        long runId,
        string jobName,
        string authorization,
        CancellationToken ct
    )
    {
        for (int page = 1; ; page++)
        {
            var result = await SendAsync(
                HttpMethod.Get,
                RepoPath(repo) + $"/actions/runs/{runId}/jobs?filter=latest&per_page=100&page={page}",
                authorization,
                null,
                ct
            );
            var jobs = result.GetProperty("jobs");
            var matching = jobs.EnumerateArray().Where(job => job.GetProperty("name").GetString() == jobName).ToArray();
            if (matching.Length != 0)
                return matching[0].GetProperty("status").GetString() == "completed"
                    ? matching[0].GetProperty("conclusion").GetString()
                    : null;
            if (jobs.GetArrayLength() < 100)
                throw new AgwException(ErrorCodes.InvalidParam, "The configured workflow job was not found.");
            if (page >= 10)
                throw new AgwException(ErrorCodes.InvalidParam, "The workflow job list exceeds the supported limit.");
        }
    }

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        string authorization,
        JsonNode? body,
        CancellationToken ct
    )
    {
        using var response = await SendResponseAsync(method, path, authorization, body, ct);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, ct);
    }

    private async Task<HttpResponseMessage> SendResponseAsync(
        HttpMethod method,
        string path,
        string authorization,
        JsonNode? body,
        CancellationToken ct
    )
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri("https://api.github.com/"), path));
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("Agw-TaskPilot/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var client = _http.CreateClient("taskpilot.github");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        return document.RootElement.Clone();
    }

    private static string RepoPath(string repository)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
            throw new AgwException(ErrorCodes.InvalidParam, "The GitHub repository name is invalid.");
        return $"repos/{Escape(parts[0])}/{Escape(parts[1])}";
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
