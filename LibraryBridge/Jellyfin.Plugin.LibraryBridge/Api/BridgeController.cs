using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Common.Api;

namespace Jellyfin.Plugin.LibraryBridge.Api;

[ApiController]
[Route("LibraryBridge")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class BridgeController(BridgeCoordinator coordinator) : ControllerBase
{
    [HttpGet("Status")]
    public ActionResult Status() => Ok(new { Enabled = Plugin.Instance?.Configuration.Enabled == true,
        coordinator.LastReport, coordinator.LastError, TargetServer = "12.4.0" });

    [HttpPost("Preview")]
    public async Task<ActionResult<BridgeReport>> Preview(CancellationToken cancellationToken)
        => await Run(() => coordinator.PreviewAsync(cancellationToken)).ConfigureAwait(false);

    [HttpPost("Apply")]
    public async Task<ActionResult<BridgeReport>> Apply(CancellationToken cancellationToken)
        => await Run(() => coordinator.ApplyAsync(true, cancellationToken)).ConfigureAwait(false);

    [HttpPost("Restore")]
    public async Task<ActionResult> Restore(CancellationToken cancellationToken)
    {
        try { await coordinator.RestoreAsync(cancellationToken).ConfigureAwait(false); return NoContent(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException)
        { return Problem(error.Message, statusCode: 409); }
    }
    private async Task<ActionResult<BridgeReport>> Run(Func<Task<BridgeReport>> action)
    {
        try { return Ok(await action().ConfigureAwait(false)); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException)
        { return Problem(error.Message, statusCode: 409); }
    }
}
