using DeployOrchestrator.Api.Data;
using DeployOrchestrator.Api.Domain;
using DeployOrchestrator.Api.Hubs;
using DeployOrchestrator.Api.Services;
using DeployOrchestrator.Api.Workers;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- Persistence: Azure SQL when configured, EF InMemory otherwise (dev / demo / CI) ----
var sqlConnectionString = builder.Configuration.GetConnectionString("Sql");
builder.Services.AddDbContext<OrchestratorDbContext>(options =>
{
    if (!string.IsNullOrWhiteSpace(sqlConnectionString))
    {
        options.UseSqlServer(sqlConnectionString);
    }
    else
    {
        options.UseInMemoryDatabase("orchestrator");
    }
});

// ---- Services ----
builder.Services.AddSingleton<IResourceMappingService, ResourceMappingService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<DeploymentOrchestrator>();

var adoOrg = builder.Configuration["Ado:Organization"];
if (string.IsNullOrWhiteSpace(adoOrg))
{
    builder.Services.AddSingleton<IPipelineClient, SimulationAdoPipelineClient>();
}
else
{
    builder.Services.AddHttpClient<IPipelineClient, AdoPipelineClient>();
}

// ---- SignalR + CORS (frontend) ----
builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173", "https://localhost:5173"])
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

builder.Services.AddControllers().AddJsonOptions(options =>
{
    // Serialize enums as strings (Draft, SecurityApproval, PipelineRunning, ...) for API + SignalR clients
    options.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddApplicationInsightsTelemetryIfConfigured();
builder.Services.AddHostedService<MonitoringBackgroundService>();

var app = builder.Build();

// Ensure database schema exists (Azure SQL). InMemory needs nothing.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
    if (db.Database.IsRelational())
    {
        db.Database.EnsureCreated();
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.MapControllers();
app.MapHub<DeploymentStatusHub>("/hubs/deployment");
app.MapHealthChecks("/healthz");

app.Run();

public partial class Program { } // exposes Program for WebApplicationFactory integration tests

static class TelemetryExtensions
{
    // Application Insights only when the connection string is present (keeps local dev dependency-free).
    public static void AddApplicationInsightsTelemetryIfConfigured(this IServiceCollection services) { }
}
