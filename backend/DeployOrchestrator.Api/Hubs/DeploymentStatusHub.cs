using Microsoft.AspNetCore.SignalR;

namespace DeployOrchestrator.Api.Hubs;

/// <summary>
/// SignalR hub. Clients join a group per deployment request id and receive
/// "statusUpdated" events for live progress ("Push status over SignalR" in the diagram).
/// </summary>
public class DeploymentStatusHub : Hub
{
    public Task Subscribe(string requestId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, requestId);

    public Task Unsubscribe(string requestId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, requestId);
}
