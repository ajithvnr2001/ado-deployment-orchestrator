using DeployOrchestrator.Api.Data;
using DeployOrchestrator.Api.Domain;
using DeployOrchestrator.Api.Hubs;
using DeployOrchestrator.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeployOrchestrator.Tests;

/// <summary>
/// Full workflow engine tests against the simulated pipeline client:
/// start -> inference -> validation -> missing inputs -> approvals -> pipeline -> monitoring -> completion.
/// </summary>
public class DeploymentOrchestratorTests : IAsyncDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly DeploymentOrchestrator _orchestrator;
    private readonly SimulationAdoPipelineClient _pipeline = new();

    public DeploymentOrchestratorTests()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new OrchestratorDbContext(options);
        var hub = new MockHubContext();
        _orchestrator = new DeploymentOrchestrator(
            _db, new ResourceMappingService(), _pipeline,
            new AuditService(_db), hub, NullLogger<DeploymentOrchestrator>.Instance);
    }

    [Fact]
    public async Task Start_WithAllFields_PassesValidation_AndAwaitsSecurityApproval()
    {
        var request = await _orchestrator.StartAsync(new StartDeploymentCommand(
            "user@contoso.com", "sub-payments", "dev", "WebApp", "payments-api",
            new Dictionary<string, string> { ["resource_group"] = "rg-payments", ["app_name"] = "app-pay", ["runtime"] = "dotnet" }));

        Assert.Equal(RequestLifecycleStatus.SecurityApproval, request.Status);
        Assert.Equal(ResourceType.WebApp, request.ResourceType);
        Assert.Empty(request.MissingFields);
        Assert.Equal("103", request.PipelineId);
        Assert.Equal("terraform/webapp", request.TerraformScriptFolder);
    }

    [Fact]
    public async Task Start_WithoutType_InfersResourceType()
    {
        var request = await _orchestrator.StartAsync(new StartDeploymentCommand(
            "user@contoso.com", "sub-x", "dev", null, "reporting sql db", new Dictionary<string, string>()));

        Assert.Equal(ResourceType.SqlDatabase, request.ResourceType);
        Assert.Equal(RequestLifecycleStatus.Draft, request.Status); // missing required fields
        Assert.Contains("server_name", request.MissingFields);
        Assert.Contains("database_name", request.MissingFields);
    }

    [Fact]
    public async Task Start_MissingBasics_ReportsValidationError()
    {
        var request = await _orchestrator.StartAsync(new StartDeploymentCommand("user@contoso.com", null, null, "WebApp", null, null));
        Assert.Equal(RequestLifecycleStatus.Validation, request.Status);
        Assert.NotNull(request.ValidationError);
        Assert.Contains("subscription is required", request.ValidationError);
        Assert.Contains("state (environment) is required", request.ValidationError);
    }

    [Fact]
    public async Task SubmitInputs_MergesValuesAndRevalidates()
    {
        var request = await _orchestrator.StartAsync(new StartDeploymentCommand(
            "user@contoso.com", "sub-x", "dev", "KeyVault", "kv-secrets", new Dictionary<string, string> { ["resource_group"] = "rg-kv" }));
        Assert.Equal(RequestLifecycleStatus.Draft, request.Status);

        var updated = await _orchestrator.SubmitInputsAsync(request.Id, new SubmitInputsCommand(new Dictionary<string, string> { ["vault_name"] = "kv-secrets-01" }));

        Assert.Equal(RequestLifecycleStatus.SecurityApproval, updated.Status);
        Assert.DoesNotContain("vault_name", updated.MissingFields);
    }

    [Fact]
    public async Task Approvals_Sequence_TriggersPipelineAndCompletes()
    {
        var request = await _orchestrator.StartAsync(new StartDeploymentCommand(
            "user@contoso.com", "sub-x", "dev", "StorageAccount", "saccount-demo",
            new Dictionary<string, string> { ["resource_group"] = "rg-demo", ["storage_name"] = "sademo01" }));

        await _orchestrator.ApproveAsync(request.Id, new ApprovalCommand("security@contoso.com", "ok"));
        var afterReview = await _orchestrator.ApproveAsync(request.Id, new ApprovalCommand("lead@contoso.com", "ship it"));

        Assert.Equal(RequestLifecycleStatus.PipelineRunning, afterReview.Status);
        Assert.NotNull(afterReview.PipelineRunId);

        // Simulate pipeline completion
        await Task.Delay(2200);
        var completed = await CompleteViaMonitoringTestHook(afterReview.Id);
        Assert.Equal(RequestLifecycleStatus.CompletedSuccess, completed.Status);
        Assert.Contains("succeeded", completed.StatusMessage);
    }

    [Fact]
    public async Task Approve_OutOfOrder_IsRejected()
    {
        // Request with missing required fields stays in Draft — approving it must be rejected.
        var request = await _orchestrator.StartAsync(new StartDeploymentCommand(
            "user@contoso.com", "sub-x", "dev", "WebApp", "app-x", new Dictionary<string, string>()));

        Assert.Equal(RequestLifecycleStatus.Draft, request.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _orchestrator.ApproveAsync(request.Id, new ApprovalCommand("lead@contoso.com", null)));
    }

    /// <summary>Test hook that mimics MonitoringBackgroundService.PollActiveRunsAsync using the simulated client.</summary>
    internal async Task<DeploymentRequest> CompleteViaMonitoringTestHook(Guid id)
    {
        var status = await _pipeline.GetRunStatusAsync(_db.DeploymentRequests.First(r => r.Id == id).PipelineRunId!.Value);
        var success = status!.Result == "succeeded";
        await _orchestrator.CompleteAsync(id, success, $"Pipeline run {status.RunId} completed with result '{status.Result}'");
        return await _orchestrator.GetAsync(id);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();
}

/// <summary>No-op hub context so tests run without a server.</summary>
internal class MockHubContext : IHubContext<DeploymentStatusHub>
{
    public IHubClients Clients { get; } = new MockClients();
    public IGroupManager Groups { get; } = new MockGroups();
}

internal class MockClients : IHubClients { public IClientProxy All { get; } = new MockProxy(); public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new MockProxy(); public IClientProxy Client(string connectionId) => new MockProxy(); public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new MockProxy(); public IClientProxy Connection(string connectionId) => new MockProxy(); public IClientProxy Connections(IReadOnlyList<string> connectionIds) => new MockProxy(); public IClientProxy Group(string groupName) => new MockProxy(); public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new MockProxy(); public IClientProxy Groups(IReadOnlyList<string> groupNames) => new MockProxy(); public IClientProxy User(string userId) => new MockProxy(); public IClientProxy Users(IReadOnlyList<string> userIds) => new MockProxy(); }
internal class MockGroups : IGroupManager { public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask; public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask; }
internal class MockProxy : IClientProxy { public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask; }
