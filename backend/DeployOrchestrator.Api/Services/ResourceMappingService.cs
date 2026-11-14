namespace DeployOrchestrator.Api.Services;

/// <summary>
/// Result of resolving a resource type against the resource mapping catalogue.
/// Mirrors the "Resolve resource type / Resource type + rules" exchange in the sequence diagram.
/// </summary>
public record ResourceMapping(
    DeployOrchestrator.Api.Domain.ResourceType ResourceType,
    int PipelineId,
    string TerraformScriptFolder,
    IReadOnlyList<string> RequiredFields);

public interface IResourceMappingService
{
    /// <summary>
    /// Rule-based inference of the resource type when the caller did not supply one.
    /// (In production this is where an AI/LLM classifier can be plugged in — see docs/ARCHITECTURE.md.)
    /// </summary>
    DeployOrchestrator.Api.Domain.ResourceType InferResourceType(string? subscription, string? state, string? resourceName, IReadOnlyDictionary<string, string> fields);

    ResourceMapping? GetMapping(DeployOrchestrator.Api.Domain.ResourceType type);
}

/// <summary>
/// Static catalogue that maps resource types to the ADO pipeline that deploys them,
/// the Terraform script folder and the required tfvars fields.
/// </summary>
public class ResourceMappingService : IResourceMappingService
{
    private readonly Dictionary<DeployOrchestrator.Api.Domain.ResourceType, ResourceMapping> _catalogue = new()
    {
        [DeployOrchestrator.Api.Domain.ResourceType.Subscription] = new(DeployOrchestrator.Api.Domain.ResourceType.Subscription, 101, "terraform/subscription", new[] { "subscription", "state", "owner" }),
        [DeployOrchestrator.Api.Domain.ResourceType.ResourceGroup] = new(DeployOrchestrator.Api.Domain.ResourceType.ResourceGroup, 102, "terraform/resource-group", new[] { "subscription", "resource_group", "location" }),
        [DeployOrchestrator.Api.Domain.ResourceType.WebApp] = new(DeployOrchestrator.Api.Domain.ResourceType.WebApp, 103, "terraform/webapp", new[] { "subscription", "resource_group", "app_name", "runtime" }),
        [DeployOrchestrator.Api.Domain.ResourceType.SqlDatabase] = new(DeployOrchestrator.Api.Domain.ResourceType.SqlDatabase, 104, "terraform/sql", new[] { "subscription", "resource_group", "server_name", "database_name" }),
        [DeployOrchestrator.Api.Domain.ResourceType.KeyVault] = new(DeployOrchestrator.Api.Domain.ResourceType.KeyVault, 105, "terraform/keyvault", new[] { "subscription", "resource_group", "vault_name" }),
        [DeployOrchestrator.Api.Domain.ResourceType.StorageAccount] = new(DeployOrchestrator.Api.Domain.ResourceType.StorageAccount, 106, "terraform/storage", new[] { "subscription", "resource_group", "storage_name" }),
        [DeployOrchestrator.Api.Domain.ResourceType.VirtualMachine] = new(DeployOrchestrator.Api.Domain.ResourceType.VirtualMachine, 107, "terraform/vm", new[] { "subscription", "resource_group", "vm_name", "vm_size" }),
    };

    public DeployOrchestrator.Api.Domain.ResourceType InferResourceType(string? subscription, string? state, string? resourceName, IReadOnlyDictionary<string, string> fields)
    {
        // Rule-based inference: ordered, deterministic keyword rules on every available signal.
        var haystack = string.Join(' ',
            new[] { subscription, state, resourceName }
                .Concat(fields.Select(f => $"{f.Key}={f.Value}"))
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        if (Contains(haystack, "web", "app", "api", "site", "frontend", "backend")) return DeployOrchestrator.Api.Domain.ResourceType.WebApp;
        if (Contains(haystack, "sql", "database", "db", "mssql")) return DeployOrchestrator.Api.Domain.ResourceType.SqlDatabase;
        if (Contains(haystack, "keyvault", "vault", "secret", "key-vault")) return DeployOrchestrator.Api.Domain.ResourceType.KeyVault;
        if (Contains(haystack, "storage", "blob", "container", "saccount")) return DeployOrchestrator.Api.Domain.ResourceType.StorageAccount;
        if (Contains(haystack, "vm", "virtualmachine", "virtual-machine", "compute")) return DeployOrchestrator.Api.Domain.ResourceType.VirtualMachine;
        if (Contains(haystack, "rg", "resourcegroup", "resource-group", "group")) return DeployOrchestrator.Api.Domain.ResourceType.ResourceGroup;

        // Default: whole subscription deployment (e.g. "new subscription for team X").
        return DeployOrchestrator.Api.Domain.ResourceType.Subscription;
    }

    public ResourceMapping? GetMapping(DeployOrchestrator.Api.Domain.ResourceType type)
        => _catalogue.TryGetValue(type, out var mapping) ? mapping : null;

    private static bool Contains(string haystack, params string[] keywords) =>
        keywords.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase));
}
