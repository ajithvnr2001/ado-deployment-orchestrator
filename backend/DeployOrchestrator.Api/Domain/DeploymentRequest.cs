namespace DeployOrchestrator.Api.Domain;

public enum RequestLifecycleStatus
{
    Draft,
    Validation,
    SecurityApproval,
    ReviewTeamApproval,
    PipelineRunning,
    CompletedSuccess,
    CompletedFailed,
    Decommissioned
}

public enum ResourceType
{
    Subscription,
    ResourceGroup,
    WebApp,
    SqlDatabase,
    KeyVault,
    StorageAccount,
    VirtualMachine
}

/// <summary>
/// Aggregate root for the deployment request lifecycle shown in the sequence workflow:
/// Draft -> Validation -> SecurityApproval -> ReviewTeamApproval -> PipelineRunning -> Completed(Success/Failed)
/// </summary>
public class DeploymentRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>ADO work item id this request is attached to (created when the request starts).</summary>
    public int WorkItemId { get; set; }

    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>Azure subscription the change applies to.</summary>
    public string? Subscription { get; set; }

    /// <summary>Azure state/environment (dev, qa, prod...).</summary>
    public string? State { get; set; }

    /// <summary>Resource type resolved by rule-based / AI inference.</summary>
    public ResourceType? ResourceType { get; set; }

    /// <summary>True when the caller explicitly supplied the resource type.</summary>
    public bool ResourceTypeExplicit { get; set; }

    /// <summary>Human readable name of the resource to deploy.</summary>
    public string? ResourceName { get; set; }

    /// <summary>Free-form field bag for pipeline-specific required fields.</summary>
    public Dictionary<string, string> Fields { get; set; } = new();

    /// <summary>Fields still required before the request can be approved.</summary>
    public List<string> MissingFields { get; set; } = new();

    public RequestLifecycleStatus Status { get; set; } = RequestLifecycleStatus.Draft;

    public string? ValidationError { get; set; }

    public bool SecurityApproved { get; set; }
    public string? SecurityApprovedBy { get; set; }

    public bool ReviewApproved { get; set; }
    public string? ReviewApprovedBy { get; set; }

    /// <summary>Azure DevOps pipeline run id once triggered; null until PipelineRunning.</summary>
    public int? PipelineRunId { get; set; }

    public string? PipelineId { get; set; }

    /// <summary>Terraform script folder resolved from the resource mapping catalogue.</summary>
    public string? TerraformScriptFolder { get; set; }

    public string? StatusMessage { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
