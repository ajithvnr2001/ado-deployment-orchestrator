using System.Net.Http.Headers;
using System.Text;

namespace DeployOrchestrator.Api.Services;

public record PipelineTriggerResult(int RunId, string Status);

public record PipelineRunStatus(int RunId, string State, string? Result);

/// <summary>
/// Abstraction over the Azure DevOps pipeline execution. Two implementations:
///  - AdoPipelineClient: real REST API calls (queues a build, polls build status)
///  - SimulationAdoPipelineClient: deterministic in-memory simulation used for local
///    development, automated tests and demo runs when ADO is not configured.
/// </summary>
public interface IPipelineClient
{
    Task<PipelineTriggerResult> TriggerAsync(int pipelineId, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default);

    Task<PipelineRunStatus?> GetRunStatusAsync(int runId, CancellationToken ct = default);
}

/// <summary>
/// Real Azure DevOps REST API client.
/// https://learn.microsoft.com/rest/api/azure/devops/build/builds/queue
/// </summary>
public class AdoPipelineClient : IPipelineClient
{
    private readonly HttpClient _http;
    private readonly ILogger<AdoPipelineClient> _logger;
    private readonly string _org;
    private readonly string _project;
    private readonly string _pat;

    public AdoPipelineClient(HttpClient http, IConfiguration config, ILogger<AdoPipelineClient> logger)
    {
        _http = http;
        _logger = logger;
        _org = config["Ado:Organization"] ?? throw new InvalidOperationException("Ado:Organization is required when Ado mode is enabled");
        _project = config["Ado:Project"] ?? throw new InvalidOperationException("Ado:Project is required when Ado mode is enabled");
        _pat = config["Ado:Pat"] ?? throw new InvalidOperationException("Ado:Pat is required when Ado mode is enabled");
        _http.BaseAddress = new Uri($"https://dev.azure.com/{_org}/");
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + _pat));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public async Task<PipelineTriggerResult> TriggerAsync(int pipelineId, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        var definitionReference = new { id = pipelineId };
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            definition = definitionReference,
            sourceBranch = "refs/heads/main",
            templateParameters = parameters.ToDictionary(p => p.Key, p => (object)p.Value)
        });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"{Uri.EscapeDataString(_project)}/_apis/build/builds?api-version=7.1", content, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
        var runId = json.GetProperty("id").GetInt32();
        var status = json.GetProperty("status").GetString() ?? "notStarted";
        _logger.LogInformation("Queued ADO pipeline {PipelineId} -> run {RunId} ({Status})", pipelineId, runId, status);
        return new PipelineTriggerResult(runId, status);
    }

    public async Task<PipelineRunStatus?> GetRunStatusAsync(int runId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"{Uri.EscapeDataString(_project)}/_apis/build/builds/{runId}?api-version=7.1", ct);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
        var state = json.GetProperty("status").GetString() ?? "unknown";
        string? result = null;
        if (json.TryGetProperty("result", out var resultProp)) result = resultProp.GetString();
        return new PipelineRunStatus(runId, state, result);
    }
}

/// <summary>
/// Deterministic simulation used when Ado:Organization is not configured.
/// A run moves notStarted -> inProgress -> completed after ~3 seconds, always succeeding
/// (runs whose parameters contain "simulate_failure=true" fail — used by tests).
/// </summary>
public class SimulationAdoPipelineClient : IPipelineClient
{
    private sealed class SimulatedRun
    {
        public DateTimeOffset Started { get; init; }
        public bool Fail { get; init; }
    }

    private readonly Dictionary<int, SimulatedRun> _runs = new();
    private int _nextRunId = 1000;

    public Task<PipelineTriggerResult> TriggerAsync(int pipelineId, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        var runId = Interlocked.Increment(ref _nextRunId);
        _runs[runId] = new SimulatedRun
        {
            Started = DateTimeOffset.UtcNow,
            Fail = parameters.TryGetValue("simulate_failure", out var f) && f == "true"
        };
        return Task.FromResult(new PipelineTriggerResult(runId, "notStarted"));
    }

    public Task<PipelineRunStatus?> GetRunStatusAsync(int runId, CancellationToken ct = default)
    {
        if (!_runs.TryGetValue(runId, out var run)) return Task.FromResult<PipelineRunStatus?>(null);
        var elapsed = DateTimeOffset.UtcNow - run.Started;
        var state = elapsed < TimeSpan.FromSeconds(2)
            ? (elapsed < TimeSpan.FromSeconds(1) ? "notStarted" : "inProgress")
            : "completed";
        string? result = state == "completed" ? (run.Fail ? "failed" : "succeeded") : null;
        return Task.FromResult<PipelineRunStatus?>(new PipelineRunStatus(runId, state, result));
    }
}
