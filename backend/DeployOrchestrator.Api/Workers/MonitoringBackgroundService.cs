using DeployOrchestrator.Api.Data;
using DeployOrchestrator.Api.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DeployOrchestrator.Api.Workers;

/// <summary>
/// Phase 5 of the sequence diagram — asynchronous monitoring.
/// Every 15 seconds (configurable) it polls every active pipeline run for up to
/// 40 minutes (configurable) and pushes live status over SignalR. When a run
/// reaches a terminal state the orchestrator completes the request.
/// </summary>
public class MonitoringBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MonitoringBackgroundService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;

    public MonitoringBackgroundService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<MonitoringBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(config.GetValue("Monitoring:IntervalSeconds", 15));
        _timeout = TimeSpan.FromMinutes(config.GetValue("Monitoring:PollTimeoutMinutes", 40));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Monitoring background service started (interval {Interval}, timeout {Timeout})", _interval, _timeout);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollActiveRunsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monitoring poll cycle failed");
            }
            await Task.Delay(_interval, stoppingToken);
        }
        _logger.LogInformation("Monitoring background service stopped");
    }

    internal async Task PollActiveRunsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var pipeline = scope.ServiceProvider.GetRequiredService<Services.IPipelineClient>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<Services.DeploymentOrchestrator>();

        var active = await db.DeploymentRequests
            .Where(r => r.Status == RequestLifecycleStatus.PipelineRunning && r.PipelineRunId != null)
            .ToListAsync(ct);

        foreach (var request in active)
        {
            var age = DateTimeOffset.UtcNow - request.UpdatedUtc;
            if (age > _timeout)
            {
                await orchestrator.CompleteAsync(request.Id, false, $"Monitoring timed out after {_timeout.TotalMinutes} minutes (run {request.PipelineRunId})", ct);
                continue;
            }

            var status = await pipeline.GetRunStatusAsync(request.PipelineRunId!.Value, ct);
            if (status is null)
            {
                _logger.LogWarning("Run {RunId} not found for request {RequestId}", request.PipelineRunId, request.Id);
                continue;
            }

            _logger.LogInformation("Run {RunId}: state={State} result={Result}", status.RunId, status.State, status.Result);

            if (status.State == "completed")
            {
                var success = status.Result == "succeeded" || status.Result == "partiallySucceeded";
                await orchestrator.CompleteAsync(request.Id, success,
                    $"Pipeline run {status.RunId} completed with result '{status.Result}'", ct);
            }
            else
            {
                // Push intermediate (running) status over SignalR
                var hub = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.DeploymentStatusHub>>();
                await hub.Clients.Group(request.Id.ToString()).SendAsync("statusUpdated",
                    new DeploymentStatusUpdate(request.Id, request.Status,
                        $"Run {status.RunId}: {status.State}", DateTimeOffset.UtcNow), ct);
            }
        }
    }
}
