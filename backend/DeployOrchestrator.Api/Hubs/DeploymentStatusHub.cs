using Microsoft.AspNetCore.SignalR;

namespace DeployOrchestrator.Api.Hubs;

/// <summary>
/// SignalR hub. Clients join a group per deployment request id and receive
/// "statusUpdated" events for live progress ("Push status over SignalR" in the diagram).
///
/// Group join works two ways:
///  1. Automatically when connecting with ?requestId=&lt;id&gt; in the query string
///     (no client->server message needed — works over SSE-only transports and
///     environments where client invocations are unavailable).
///  2. Via the Subscribe(requestId) hub method (client->server invocation).
/// </summary>
public class DeploymentStatusHub : Hub
{
    public override Task OnConnectedAsync()
    {
        var requestId = Context.GetHttpContext()?.Request.Query["requestId"].ToString();
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            return Groups.AddToGroupAsync(Context.ConnectionId, requestId);
        }
        return base.OnConnectedAsync();
    }

    public Task Subscribe(string requestId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, requestId);

    public Task Unsubscribe(string requestId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, requestId);
}
