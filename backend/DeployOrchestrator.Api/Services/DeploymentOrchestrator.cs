using DeployOrchestrator.Api.Data;
using DeployOrchestrator.Api.Domain;
using DeployOrchestrator.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DeployOrchestrator.Api.Services;

/// <summary>
/// Core workflow engine. Implements the five phases of the sequence diagram:
///  1. Deployment initiation
///  2. Resource identification &amp; validation (approve &amp; valid?)
///  3. Missing input handling (required fields missing? -> request -> submit -> merge &amp; validate)
///  4. Terraform &amp; pipeline execution (find script folder, commit tfvars, trigger pipeline)
///  5. Asynchronous monitoring (delegated to MonitoringBackgroundService)
/// Every state transition is audited and pushed to SignalR subscribers in real time.
/// </summary>
public class DeploymentOrchestrator
{
    private readonly OrchestratorDbContext _db;
    private readonly IResourceMappingService _mapping;
    private readonly IPipelineClient _pipeline;
    private readonly IAuditService _audit;
    private readonly IHubContext<DeploymentStatusHub> _hub;
    private readonly ILogger<DeploymentOrchestrator> _logger;

    public DeploymentOrchestrator(
        OrchestratorDbContext db,
        IResourceMappingService mapping,
        IPipelineClient pipeline,
        IAuditService audit,
        IHubContext<DeploymentStatusHub> hub,
        ILogger<DeploymentOrchestrator> logger)
    {
        _db = db;
        _mapping = mapping;
        _pipeline = pipeline;
        _audit = audit;
        _hub = hub;
        _logger = logger;
    }

    // ---- Phase 1: Deployment initiation ----
    public async Task<DeploymentRequest> StartAsync(StartDeploymentCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
            throw new ArgumentException("requestedBy is required");

        var request = new DeploymentRequest
        {
            RequestedBy = command.RequestedBy,
            Subscription = command.Subscription,
            State = command.State,
            ResourceName = command.ResourceName,
            Fields = command.Fields?.ToDictionary(f => f.Key, f => f.Value) ?? new(),
            Status = RequestLifecycleStatus.Validation,
        };

        if (!string.IsNullOrWhiteSpace(command.ResourceType) &&
            Enum.TryParse<ResourceType>(command.ResourceType, true, out var explicitType))
        {
            request.ResourceType = explicitType;
            request.ResourceTypeExplicit = true;
        }

        _db.DeploymentRequests.Add(request);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(request.Id, "start", $"Deployment requested by {request.RequestedBy}", ct);
        await _hub.Clients.Group(request.Id.ToString()).SendAsync("statusUpdated", ToUpdate(request, "Deployment request created"), ct);

        return await ValidateAsync(request, ct);
    }

    // ---- Phase 2: Resource identification & validation ----
    private async Task<DeploymentRequest> ValidateAsync(DeploymentRequest request, CancellationToken ct)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Subscription)) errors.Add("subscription is required");
        if (string.IsNullOrWhiteSpace(request.State)) errors.Add("state (environment) is required");

        // AI / rule-based inference if type missing
        if (request.ResourceType is null)
        {
            request.ResourceType = _mapping.InferResourceType(request.Subscription, request.State, request.ResourceName, request.Fields);
            await _audit.LogAsync(request.Id, "infer", $"Resource type inferred as {request.ResourceType}", ct);
        }

        var mapping = _mapping.GetMapping(request.ResourceType.Value)
            ?? throw new InvalidOperationException($"No mapping for {request.ResourceType}");

        request.PipelineId = mapping.PipelineId.ToString();
        request.TerraformScriptFolder = mapping.TerraformScriptFolder;

        // Required fields missing? Top-level Subscription/State satisfy the 'subscription'/'state' fields.
        var effectiveFields = new Dictionary<string, string>(request.Fields);
        if (!string.IsNullOrWhiteSpace(request.Subscription)) effectiveFields.TryAdd("subscription", request.Subscription);
        if (!string.IsNullOrWhiteSpace(request.State)) effectiveFields.TryAdd("state", request.State);
        var missing = mapping.RequiredFields
            .Where(f => !effectiveFields.ContainsKey(f) || string.IsNullOrWhiteSpace(effectiveFields[f]))
            .ToList();
        request.MissingFields = missing;

        if (errors.Count > 0)
        {
            request.Status = RequestLifecycleStatus.Validation;
            request.ValidationError = string.Join("; ", errors);
            request.MissingFields = request.MissingFields.Concat(errors).Distinct().ToList();
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(request.Id, "validation-failed", request.ValidationError, ct);
            await _hub.Clients.Group(request.Id.ToString()).SendAsync("statusUpdated", ToUpdate(request, $"Validation failed: {request.ValidationError}"));
            return request;
        }

        if (missing.Count > 0)
        {
            request.Status = RequestLifecycleStatus.Draft;
            request.ValidationError = null;
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(request.Id, "missing-inputs", $"Required fields missing: {string.Join(", ", missing)}", ct);
            await _hub.Clients.Group(request.Id.ToString()).SendAsync("statusUpdated", ToUpdate(request, $"Required fields missing: {string.Join(", ", missing)}"));
            return request;
        }

        // Approved & valid? -> move to security approval gate
        request.Status = RequestLifecycleStatus.SecurityApproval;
        request.ValidationError = null;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(request.Id, "validation-passed", "Request valid; awaiting security approval", ct);
        await _hub.Clients.Group(request.Id.ToString()).SendAsync("statusUpdated", ToUpdate(request, "Validation passed — awaiting security approval"));
        return request;
    }

    // ---- Phase 3: Missing input handling ----
    public async Task<DeploymentRequest> SubmitInputsAsync(Guid id, SubmitInputsCommand command, CancellationToken ct = default)
    {
        var request = await GetAsync(id, ct);
        foreach (var (key, value) in command.Fields)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Field '{key}' cannot be empty");
            request.Fields[key] = value;
            request.MissingFields.Remove(key);
        }

        request.UpdatedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(id, "inputs-submitted", $"Fields: {string.Join(", ", command.Fields.Keys)}", ct);

        // Merge values & validate -> re-run validation to close the loop
        return await ValidateAsync(request, ct);
    }

    // ---- Approval gates (Security then Review Team) ----
    public async Task<DeploymentRequest> ApproveAsync(Guid id, ApprovalCommand command, CancellationToken ct = default)
    {
        var request = await GetAsync(id, ct);

        switch (request.Status)
        {
            case RequestLifecycleStatus.SecurityApproval:
                request.SecurityApproved = true;
                request.SecurityApprovedBy = command.ApprovedBy;
                request.Status = RequestLifecycleStatus.ReviewTeamApproval;
                await _audit.LogAsync(id, "security-approved", $"By {command.ApprovedBy}. {command.Comment}", ct);
                break;

            case RequestLifecycleStatus.ReviewTeamApproval:
                request.ReviewApproved = true;
                request.ReviewApprovedBy = command.ApprovedBy;
                await _audit.LogAsync(id, "review-approved", $"By {command.ApprovedBy}. {command.Comment}", ct);
                await TriggerPipelineAsync(request, ct);
                break;

            default:
                throw new InvalidOperationException($"Request {id} is not awaiting approval (status: {request.Status})");
        }

        request.UpdatedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _hub.Clients.Group(id.ToString()).SendAsync("statusUpdated", ToUpdate(request, request.StatusMessage ?? $"Status: {request.Status}"));
        return request;
    }

    // ---- Phase 4: Terraform & pipeline execution ----
    private async Task TriggerPipelineAsync(DeploymentRequest request, CancellationToken ct)
    {
        request.Status = RequestLifecycleStatus.PipelineRunning;
        request.StatusMessage = "Triggering deployment pipeline";

        var mapping = _mapping.GetMapping(request.ResourceType!.Value)!;
        var parameters = new Dictionary<string, string>(request.Fields)
        {
            ["request_id"] = request.Id.ToString(),
            ["terraform_script_folder"] = mapping.TerraformScriptFolder,
            ["work_item_id"] = request.WorkItemId.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(request.Subscription)) parameters["subscription"] = request.Subscription;
        if (!string.IsNullOrWhiteSpace(request.State)) parameters["state"] = request.State;
        // Commit terraform.tfvars equivalent: tfvars content is derived from fields by the pipeline.
        parameters["tfvars_json"] = System.Text.Json.JsonSerializer.Serialize(request.Fields);

        var trigger = await _pipeline.TriggerAsync(mapping.PipelineId, parameters, ct);
        request.PipelineRunId = trigger.RunId;
        request.StatusMessage = $"Pipeline run {trigger.RunId} queued";
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(request.Id, "pipeline-queued", $"ADO pipeline {mapping.PipelineId}, run {trigger.RunId}", ct);
        await _hub.Clients.Group(request.Id.ToString()).SendAsync("statusUpdated",
            ToUpdate(request, $"Deployment pipeline running (run {trigger.RunId}) — monitoring started"));

        // ---- Phase 5: enqueue monitoring job (handled by MonitoringBackgroundService) ----
    }

    // ---- Queries ----
    public async Task<DeploymentRequest> GetAsync(Guid id, CancellationToken ct = default)
    {
        var request = await _db.DeploymentRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        return request ?? throw new KeyNotFoundException($"Deployment request {id} not found");
    }

    public async Task<List<DeploymentRequest>> ListAsync(CancellationToken ct = default)
        => await _db.DeploymentRequests.OrderByDescending(r => r.CreatedUtc).Take(100).ToListAsync(ct);

    /// <summary>Called by the monitoring worker when a run reaches a terminal state.</summary>
    public async Task CompleteAsync(Guid id, bool success, string message, CancellationToken ct = default)
    {
        var request = await GetAsync(id, ct);
        request.Status = success ? RequestLifecycleStatus.CompletedSuccess : RequestLifecycleStatus.CompletedFailed;
        request.StatusMessage = message;
        request.UpdatedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(id, "completed", $"Result: {request.Status}. {message}", ct);
        await _hub.Clients.Group(id.ToString()).SendAsync("statusUpdated", ToUpdate(request, message));
    }

    private static DeploymentStatusUpdate ToUpdate(DeploymentRequest request, string message) =>
        new(request.Id, request.Status, message, DateTimeOffset.UtcNow);
}
