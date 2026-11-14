using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployOrchestrator.Api.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeployOrchestrator.Tests;

/// <summary>
/// End-to-end API tests over a real in-process server (WebApplicationFactory):
/// exercises HTTP endpoints + SignalR wiring + the full lifecycle of the sequence diagram.
/// </summary>
public class ApiEndToEndTests : IClassFixture<ApiEndToEndTests.FactoryFixture>
{
    /// <summary>JSON options matching the API: enums serialize as strings.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Factory with a 1-second monitoring poll so the simulated pipeline completes quickly.</summary>
    public sealed class FactoryFixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("Monitoring:IntervalSeconds", "1");
    }

    private readonly WebApplicationFactory<Program> _factory;

    public ApiEndToEndTests(FactoryFixture factory) => _factory = factory;

    [Fact]
    public async Task HealthCheck_ReturnsHealthy()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/healthz");
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task FullLifecycle_ThroughHttpEndpoints()
    {
        var client = _factory.CreateClient();

        // Phase 1: start
        var startResponse = await client.PostAsJsonAsync("/api/deployments", new
        {
            requestedBy = "e2e@contoso.com",
            subscription = "sub-e2e",
            state = "qa",
            resourceType = "StorageAccount",
            resourceName = "saccount-e2e",
            fields = new Dictionary<string, string> { ["resource_group"] = "rg-e2e", ["storage_name"] = "sae2e01" }
        });
        Assert.True(startResponse.IsSuccessStatusCode, await startResponse.Content.ReadAsStringAsync());
        var request = await startResponse.Content.ReadFromJsonAsync<DeploymentRequestResponse>(JsonOpts);
        Assert.NotNull(request);
        Assert.Equal(RequestLifecycleStatus.SecurityApproval, request.Status);

        // Phase 2: security approval
        var secApproval = await client.PostAsJsonAsync($"/api/deployments/{request.Id}/approve",
            new { approvedBy = "sec@contoso.com", comment = "approved" });
        Assert.True(secApproval.IsSuccessStatusCode);
        var afterSec = await secApproval.Content.ReadFromJsonAsync<DeploymentRequestResponse>(JsonOpts);
        Assert.Equal(RequestLifecycleStatus.ReviewTeamApproval, afterSec.Status);

        // Phase 3: review approval -> pipeline triggered (simulated)
        var reviewApproval = await client.PostAsJsonAsync($"/api/deployments/{request.Id}/approve",
            new { approvedBy = "lead@contoso.com", comment = "ship it" });
        Assert.True(reviewApproval.IsSuccessStatusCode);
        var afterReview = await reviewApproval.Content.ReadFromJsonAsync<DeploymentRequestResponse>(JsonOpts);
        Assert.Equal(RequestLifecycleStatus.PipelineRunning, afterReview.Status);
        Assert.NotNull(afterReview.PipelineRunId);

        // Phase 5: wait for the simulated run to complete, then check audit trail
        var completed = false;
        for (var i = 0; i < 20 && !completed; i++)
        {
            await Task.Delay(500);
            var current = await (await client.GetAsync($"/api/deployments/{request.Id}")).Content
                .ReadFromJsonAsync<DeploymentRequestResponse>(JsonOpts);
            completed = current.Status is RequestLifecycleStatus.CompletedSuccess or RequestLifecycleStatus.CompletedFailed;
        }
        Assert.True(completed, "Request did not reach a terminal state in time");

        var audit = await (await client.GetAsync($"/api/deployments/{request.Id}/audit"))
            .Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.True(audit.Count >= 5, $"Expected at least 5 audit entries, got {audit.Count}");
    }

    [Fact]
    public async Task MissingInput_Flow_ThroughHttp()
    {
        var client = _factory.CreateClient();

        var startResponse = await client.PostAsJsonAsync("/api/deployments", new
        {
            requestedBy = "e2e@contoso.com",
            subscription = "sub-e2e",
            state = "dev",
            resourceType = "WebApp",
            resourceName = "app-missing",
            fields = new Dictionary<string, string>()
        });
        var request = await startResponse.Content.ReadFromJsonAsync<DeploymentRequestResponse>(JsonOpts);
        Assert.Equal(RequestLifecycleStatus.Draft, request.Status);
        Assert.Contains("resource_group", request.MissingFields);

        // Chat-style missing input handling
        var submit = await client.PostAsJsonAsync($"/api/deployments/{request.Id}/inputs", new
        {
            fields = new Dictionary<string, string>
            {
                ["resource_group"] = "rg-missing",
                ["app_name"] = "app-missing-01",
                ["runtime"] = "dotnet"
            }
        });
        Assert.True(submit.IsSuccessStatusCode);
        var updated = await submit.Content.ReadFromJsonAsync<DeploymentRequestResponse>(JsonOpts);
        Assert.Equal(RequestLifecycleStatus.SecurityApproval, updated.Status);
    }
}
