using Microsoft.AspNetCore.Mvc;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Api.Controllers.Admin;

public record VisitSwitch(bool Enabled);

/// <summary>Admin → Visitors: visitor numbers, sources, usage time, quietest hour and server restarts.</summary>
public class VisitorsController(VisitStatsService stats) : AdminControllerBase
{
    /// <param name="days">0 = today, 7, 30 or 90.</param>
    [HttpGet]
    public Task<VisitReport> Get(int days = 7) => stats.ReportAsync(days);

    /// <summary>Switches recording on or off (the storefront stops checking in while it is off).</summary>
    [HttpPut("switch")]
    public async Task<IActionResult> Switch(VisitSwitch r)
    {
        await stats.SetEnabledAsync(r.Enabled);
        return NoContent();
    }
}
