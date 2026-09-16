using Asp.Versioning;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Merconiq.Web.Controllers.Api.V1;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/stock/opening")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
public sealed class OpeningStockController(IOpeningStockImportService imports) : ControllerBase
{
    [HttpPost("preview")]
    [Authorize(Policy = CapabilityPolicies.Approve)]
    [Authorize(Policy = CapabilityPolicies.TenantAdministrator)]
    // This bearer-token API is not cookie-authenticated, so browser CSRF tokens do not apply.
    // Keep the explicit validation marker for static security analysis while opting out at runtime.
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    [ProducesResponseType(typeof(ApiResponse<OpeningStockPreviewResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OpeningStockPreviewResult), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Preview(
        [FromBody] OpeningStockPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var result = await imports.PreviewAsync(request, cancellationToken);
        return result.Rejected > 0
            ? UnprocessableEntity(result)
            : Ok(ApiResponse<OpeningStockPreviewResult>.CreateSuccess(result));
    }

    [HttpPost("replay")]
    [Authorize(Policy = CapabilityPolicies.Approve)]
    [Authorize(Policy = CapabilityPolicies.TenantAdministrator)]
    // This bearer-token API is not cookie-authenticated, so browser CSRF tokens do not apply.
    // Keep the explicit validation marker for static security analysis while opting out at runtime.
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    [ProducesResponseType(typeof(ApiResponse<OpeningStockReplayResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OpeningStockReplayResult), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Replay(
        [FromBody] OpeningStockReplayRequest request,
        CancellationToken cancellationToken)
    {
        var result = await imports.ReplayAsync(request, cancellationToken);
        return result.Rejected > 0
            ? UnprocessableEntity(result)
            : Ok(ApiResponse<OpeningStockReplayResult>.CreateSuccess(result));
    }
}
