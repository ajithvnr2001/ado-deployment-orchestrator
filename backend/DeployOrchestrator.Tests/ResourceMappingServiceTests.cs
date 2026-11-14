using DeployOrchestrator.Api.Domain;
using DeployOrchestrator.Api.Services;
using Xunit;

namespace DeployOrchestrator.Tests;

public class ResourceMappingServiceTests
{
    private readonly ResourceMappingService _svc = new();

    [Theory]
    [InlineData("create a new web app for the payments team", ResourceType.WebApp)]
    [InlineData("provision sql database for reporting", ResourceType.SqlDatabase)]
    [InlineData("need a key vault for the secrets of state prod", ResourceType.KeyVault)]
    [InlineData("spin up a storage account with blob container", ResourceType.StorageAccount)]
    [InlineData("deploy a virtual machine vm_size D4s_v3", ResourceType.VirtualMachine)]
    [InlineData("new resource group in westeurope", ResourceType.ResourceGroup)]
    [InlineData("new subscription for team alpha", ResourceType.Subscription)]
    public void InferResourceType_ClassifiesKeywords(string input, ResourceType expected)
    {
        var result = _svc.InferResourceType(input, null, null, new Dictionary<string, string>());
        Assert.Equal(expected, result);
    }

    [Fact]
    public void InferResourceType_UsesFieldValues_AsSignals()
    {
        var result = _svc.InferResourceType(null, null, null, new Dictionary<string, string> { ["note"] = "hosting an api backend" });
        Assert.Equal(ResourceType.WebApp, result);
    }

    [Theory]
    [InlineData(ResourceType.WebApp, 103, "terraform/webapp", new[] { "subscription", "resource_group", "app_name", "runtime" })]
    [InlineData(ResourceType.SqlDatabase, 104, "terraform/sql", new[] { "subscription", "resource_group", "server_name", "database_name" })]
    public void GetMapping_ReturnsPipelineAndRequiredFields(ResourceType type, int pipelineId, string folder, string[] required)
    {
        var mapping = _svc.GetMapping(type);
        Assert.NotNull(mapping);
        Assert.Equal(pipelineId, mapping.PipelineId);
        Assert.Equal(folder, mapping.TerraformScriptFolder);
        Assert.Equal(required, mapping.RequiredFields);
    }
}
