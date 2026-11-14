using System.Text.Json.Serialization;

namespace DeployOrchestrator.Api.Domain;

public record StartDeploymentCommand(
    string RequestedBy,
    string? Subscription,
    string? State,
    string? ResourceType,
    string? ResourceName,
    Dictionary<string, string>? Fields);

public record SubmitInputsCommand(Dictionary<string, string> Fields);

public record ApprovalCommand(string ApprovedBy, string? Comment);

public record MissingInputsResponse(
    Guid RequestId,
    RequestLifecycleStatus Status,
    IReadOnlyList<string> MissingFields,
    string Message);

public record DeploymentRequestResponse(
    Guid Id,
    int WorkItemId,
    string RequestedBy,
    string? Subscription,
    string? State,
    [property: JsonPropertyName("resourceType")] string? ResourceType,
    string? ResourceName,
    Dictionary<string, string> Fields,
    IReadOnlyList<string> MissingFields,
    RequestLifecycleStatus Status,
    string? ValidationError,
    bool SecurityApproved,
    string? SecurityApprovedBy,
    bool ReviewApproved,
    string? ReviewApprovedBy,
    int? PipelineRunId,
    string? PipelineId,
    string? TerraformScriptFolder,
    string? StatusMessage,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public static DeploymentRequestResponse From(DeploymentRequest r) => new(
        r.Id,
        r.WorkItemId,
        r.RequestedBy,
        r.Subscription,
        r.State,
        r.ResourceType?.ToString(),
        r.ResourceName,
        r.Fields,
        r.MissingFields,
        r.Status,
        r.ValidationError,
        r.SecurityApproved,
        r.SecurityApprovedBy,
        r.ReviewApproved,
        r.ReviewApprovedBy,
        r.PipelineRunId,
        r.PipelineId,
        r.TerraformScriptFolder,
        r.StatusMessage,
        r.CreatedUtc,
        r.UpdatedUtc);
}

/// <summary>Status update pushed to clients over SignalR (group = request id).</summary>
public record DeploymentStatusUpdate(
    Guid RequestId,
    RequestLifecycleStatus Status,
    string? Message,
    DateTimeOffset TimestampUtc);
