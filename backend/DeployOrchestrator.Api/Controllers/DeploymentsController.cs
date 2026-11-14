using DeployOrchestrator.Api.Domain;
using DeployOrchestrator.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeployOrchestrator.Api.Controllers;

[ApiController]
[Route("api/deployments")]
public class DeploymentsController : ControllerBase
{
    private readonly DeploymentOrchestrator _orchestrator;
    private readonly IAuditService _audit;

    public DeploymentsController(DeploymentOrchestrator orchestrator, IAuditService audit)
    {
        _orchestrator = orchestrator;
        _audit = audit;
    }

    /// <summary>StartDeploymentCommand — begins phase 1 of the workflow.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(DeploymentRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Start([FromBody] StartDeploymentCommand command, CancellationToken ct)
    {
        try
        {
            var request = await _orchestrator.StartAsync(command, ct);
            return Ok(DeploymentRequestResponse.From(request));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>SubmitInputsCommand — phase 3 missing input handling.</summary>
    [HttpPost("{id:guid}/inputs")]
    public async Task<IActionResult> SubmitInputs(Guid id, [FromBody] SubmitInputsCommand command, CancellationToken ct)
    {
        try
        {
            var request = await _orchestrator.SubmitInputsAsync(id, command, ct);
            return Ok(DeploymentRequestResponse.From(request));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Approval gate (security, then review team).</summary>
    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ApprovalCommand command, CancellationToken ct)
    {
        try
        {
            var request = await _orchestrator.ApproveAsync(id, command, ct);
            return Ok(DeploymentRequestResponse.From(request));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        try
        {
            var request = await _orchestrator.GetAsync(id, ct);
            return Ok(DeploymentRequestResponse.From(request));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await _orchestrator.ListAsync(ct)).Select(DeploymentRequestResponse.From));

    /// <summary>Complete audit &amp; history for a request.</summary>
    [HttpGet("{id:guid}/audit")]
    public async Task<IActionResult> Audit(Guid id, CancellationToken ct) =>
        Ok(await _audit.GetAsync(id, ct));
}
