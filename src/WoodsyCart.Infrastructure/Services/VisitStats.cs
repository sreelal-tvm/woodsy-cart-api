using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;

namespace WoodsyCart.Infrastructure.Services;

/// <summary>The storefront's once-a-minute check-in. VisitId empty starts a new visit; Seconds is time on screen since the last one.</summary>
public record VisitPing(Guid VisitorId, int? VisitId, string? Source, int Seconds);
/// <summary>On false the storefront stops checking in (switched off in Admin, a bot, or a staff account).</summary>
public record VisitPingResult(int? VisitId, bool On);

public record VisitTotals(int People, int Guests, int Registered, int Visits, long Seconds, int OnlineNow);
public record VisitSourceRow(string Source, int People, int Guests, int Registered, int Visits, long Seconds);
public record VisitUserRow(int Id, string FullName, string? Email, string? Phone, int Visits, long Seconds, DateTime LastSeenAt, int Orders);
public record ServerRestart(DateTime DownAt, DateTime UpAt, int Minutes);
/// <summary>Heat: [day 0 = Monday][hour 0–23] average people on the site (IST), null where there's no data yet.</summary>
public record QuietTime(double?[][] Heat, int? QuietHour, double? QuietAverage, double DaysOfData);
public record VisitReport(bool Enabled, DateTime? OffSince, VisitTotals Totals, List<VisitSourceRow> Sources,
    List<VisitUserRow> Users, QuietTime Quiet, DateTime? RunningSince, List<ServerRestart> Restarts);

/// <summary>
/// Admin → Visitors: how many people use the store, where they came from, how long they stay, and the quietest hour
/// for restarting the laptop. Kept light: one row per visit, updated once a minute while the site is on screen.
/// </summary>
public partial class VisitStatsService(AppDbContext db, IMemoryCache cache)
{
    private const string EnabledKey = "visit-stats-on";
    /// <summary>A visit ends after this long without a check-in.</summary>
    public static readonly TimeSpan VisitGap = TimeSpan.FromMinutes(30);
    private const int HeatDays = 56;

    [GeneratedRegex(@"bot|crawl|spider|slurp|headless|lighthouse|facebookexternalhit|preview", RegexOptions.IgnoreCase)]
    private static partial Regex BotAgent();

    public Task<bool> IsEnabledAsync() => cache.GetOrCreateAsync(EnabledKey, async e =>
    {
        e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
        return !await db.VisitStatsPauses.AnyAsync(p => p.EndedAt == null);
    });

    public async Task<VisitPingResult> PingAsync(VisitPing r, int? userId, bool staff, string? userAgent)
    {
        if (!await IsEnabledAsync()) return new(null, false);
        if (staff || r.VisitorId == Guid.Empty || (userAgent is not null && BotAgent().IsMatch(userAgent))) return new(null, false);

        var now = DateTime.UtcNow;
        var visit = r.VisitId is int id
            ? await db.Visits.FirstOrDefaultAsync(v => v.Id == id && v.VisitorId == r.VisitorId && v.LastSeenAt > now - VisitGap)
            : null;
        if (visit is null)
        {
            visit = new Visit
            {
                VisitorId = r.VisitorId, UserId = userId, LastSeenAt = now,
                Source = VisitSources.All.Contains(r.Source) ? r.Source! : VisitSources.Direct
            };
            db.Visits.Add(visit);
        }
        else
        {
            // Never more than the time that really passed, so a faulty or tampered page can't inflate the numbers.
            var passed = (int)(now - visit.LastSeenAt).TotalSeconds + 5;
            visit.ActiveSeconds += Math.Clamp(r.Seconds, 0, Math.Min(passed, 75));
            visit.LastSeenAt = now;
            visit.UserId ??= userId;
        }
        await db.SaveChangesAsync();
        return new(visit.Id, true);
    }

    public async Task SetEnabledAsync(bool on)
    {
        var open = await db.VisitStatsPauses.Where(p => p.EndedAt == null).ToListAsync();
        if (on) foreach (var p in open) p.EndedAt = DateTime.UtcNow;
        else if (open.Count == 0) db.VisitStatsPauses.Add(new VisitStatsPause());
        await db.SaveChangesAsync();
        cache.Remove(EnabledKey);
    }

    /// <param name="days">0 = today (IST), otherwise the last 7 / 30 / 90 days.</param>
    public async Task<VisitReport> ReportAsync(int days)
    {
        var now = DateTime.UtcNow;
        var from = days <= 0 ? FinancialYear.ToIst(now).Date.AddMinutes(-330) : now.AddDays(-Math.Min(days, 365));
        var openPause = await db.VisitStatsPauses.AsNoTracking().Where(p => p.EndedAt == null).OrderBy(p => p.Id).FirstOrDefaultAsync();

        var visits = await db.Visits.AsNoTracking().Where(v => v.LastSeenAt >= from)
            .Select(v => new VisitRow(v.VisitorId, v.UserId, v.Source, v.CreatedAt, v.LastSeenAt, v.ActiveSeconds)).ToListAsync();
        // A phone that signed in once counts as that customer, also for its visits before signing in.
        var linked = (await db.Visits.AsNoTracking().Where(v => v.UserId != null)
                .Select(v => new { v.VisitorId, v.UserId, v.LastSeenAt }).ToListAsync())
            .GroupBy(v => v.VisitorId).ToDictionary(g => g.Key, g => g.MaxBy(v => v.LastSeenAt)!.UserId!.Value);
        int? UserOf(VisitRow v) => v.UserId ?? (linked.TryGetValue(v.VisitorId, out var u) ? u : null);
        string Person(VisitRow v) => UserOf(v) is int u ? "u" + u : "v" + v.VisitorId;

        VisitSourceRow Sum(string source, List<VisitRow> rows)
        {
            var people = rows.GroupBy(Person).ToList();
            var registered = people.Count(g => g.Key[0] == 'u');
            return new(source, people.Count, people.Count - registered, registered, rows.Count, rows.Sum(v => (long)v.ActiveSeconds));
        }
        var all = Sum("all", visits);
        var online = await db.Visits.AsNoTracking().Where(v => v.LastSeenAt >= now.AddMinutes(-2))
            .Select(v => new VisitRow(v.VisitorId, v.UserId, v.Source, v.CreatedAt, v.LastSeenAt, v.ActiveSeconds)).ToListAsync();
        var totals = new VisitTotals(all.People, all.Guests, all.Registered, all.Visits, all.Seconds, online.Select(Person).Distinct().Count());
        var bySource = visits.GroupBy(v => v.Source).ToDictionary(g => g.Key, g => g.ToList());
        var sources = VisitSources.All.Select(s => Sum(s, bySource.GetValueOrDefault(s) ?? [])).ToList();

        var userVisits = visits.Where(v => UserOf(v) != null).GroupBy(v => UserOf(v)!.Value).ToList();
        var ids = userVisits.Select(g => g.Key).ToList();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Email, u.Phone, Orders = db.Orders.Count(o => o.UserId == u.Id) }).ToDictionaryAsync(u => u.Id);
        var users = userVisits.Where(g => people.ContainsKey(g.Key)).Select(g =>
            {
                var u = people[g.Key];
                return new VisitUserRow(u.Id, u.FullName, OrderService.IsPlaceholderEmail(u.Email) ? null : u.Email, u.Phone,
                    g.Count(), g.Sum(v => (long)v.ActiveSeconds), g.Max(v => v.LastSeenAt), u.Orders);
            })
            .OrderByDescending(u => u.LastSeenAt).Take(200).ToList();

        var runs = await db.ServerRuns.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(500)
            .Select(r => new { r.CreatedAt, r.LastAliveAt }).ToListAsync();
        var restarts = runs.Zip(runs.Skip(1), (next, prev) => new ServerRestart(prev.LastAliveAt, next.CreatedAt,
                (int)Math.Round((next.CreatedAt - prev.LastAliveAt).TotalMinutes))).Take(10).ToList();

        return new VisitReport(openPause is null, openPause?.CreatedAt, totals, sources, users,
            await QuietAsync(now, runs.Select(r => (r.CreatedAt, r.LastAliveAt)).ToList()), runs.FirstOrDefault()?.CreatedAt, restarts);
    }

    private record VisitRow(Guid VisitorId, int? UserId, string Source, DateTime StartedAt, DateTime LastSeenAt, int ActiveSeconds);

    /// <summary>
    /// Average people on the site for each weekday and hour over the last 8 weeks. Only hours when the API was running
    /// and recording was on count, so downtime and switched-off periods don't look like quiet time.
    /// </summary>
    private async Task<QuietTime> QuietAsync(DateTime now, List<(DateTime Start, DateTime End)> runs)
    {
        var from = now.AddDays(-HeatDays);
        var pauses = await db.VisitStatsPauses.AsNoTracking().Where(p => p.EndedAt == null || p.EndedAt > from)
            .Select(p => new { p.CreatedAt, p.EndedAt }).ToListAsync();
        var visits = await db.Visits.AsNoTracking().Where(v => v.LastSeenAt >= from && v.ActiveSeconds > 0)
            .Select(v => new { v.CreatedAt, v.LastSeenAt, v.ActiveSeconds }).ToListAsync();

        var covered = new double[7, 24];   // minutes recorded
        var active = new double[7, 24];    // person-minutes on screen
        foreach (var (start, end) in runs.Where(r => r.End > from))
        {
            var s = start < from ? from : start;
            Spread(covered, s, end, 1);
            foreach (var p in pauses)
            {
                var ps = p.CreatedAt > s ? p.CreatedAt : s;
                var pe = (p.EndedAt ?? now) < end ? p.EndedAt ?? now : end;
                if (pe > ps) Spread(covered, ps, pe, -1);
            }
        }
        foreach (var v in visits)
        {
            var minutes = (v.LastSeenAt - v.CreatedAt).TotalMinutes;
            // Time on screen is spread evenly over the visit (it is only known per visit, not per minute).
            if (minutes < 1) Add(active, v.CreatedAt, v.ActiveSeconds / 60.0);
            else Spread(active, v.CreatedAt, v.LastSeenAt, v.ActiveSeconds / 60.0 / minutes);
        }

        var heat = new double?[7][];
        for (var d = 0; d < 7; d++)
        {
            heat[d] = new double?[24];
            for (var h = 0; h < 24; h++) heat[d][h] = covered[d, h] >= 30 ? Math.Round(active[d, h] / covered[d, h], 2) : null;
        }
        // Quietest hour of the day across the week, once each hour has at least a week of data (7 × 60 minutes).
        int? quietHour = null; double? quietAvg = null;
        for (var h = 0; h < 24; h++)
        {
            double c = 0, a = 0;
            for (var d = 0; d < 7; d++) { c += covered[d, h]; a += active[d, h]; }
            if (c < 7 * 60) continue;
            if (quietAvg is null || a / c < quietAvg) { quietHour = h; quietAvg = a / c; }
        }
        var total = 0.0;
        foreach (var c in covered) total += c;
        return new QuietTime(heat, quietHour, quietAvg is null ? null : Math.Round(quietAvg.Value, 2), Math.Round(total / 60 / 24, 1));
    }

    /// <summary>Adds perMinute for every minute between start and end into the IST weekday × hour buckets.</summary>
    private static void Spread(double[,] buckets, DateTime start, DateTime end, double perMinute)
    {
        var t = FinancialYear.ToIst(start);
        var stop = FinancialYear.ToIst(end);
        while (t < stop)
        {
            var next = t.Date.AddHours(t.Hour + 1);
            if (next > stop) next = stop;
            buckets[Day(t), t.Hour] += (next - t).TotalMinutes * perMinute;
            t = next;
        }
    }

    private static void Add(double[,] buckets, DateTime at, double value)
    {
        var t = FinancialYear.ToIst(at);
        buckets[Day(t), t.Hour] += value;
    }

    private static int Day(DateTime t) => ((int)t.DayOfWeek + 6) % 7;   // Monday = 0
}

/// <summary>Records each run of the API (for restarts and downtime) and deletes visit statistics older than a year.</summary>
public class ServerRunWorker(IServiceScopeFactory scopes, ILogger<ServerRunWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int? runId = null;
        var cleaned = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                if (runId is null)
                {
                    var run = new ServerRun();
                    db.ServerRuns.Add(run);
                    await db.SaveChangesAsync(stoppingToken);
                    runId = run.Id;
                }
                else await db.ServerRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.LastAliveAt, now), stoppingToken);

                if (now - cleaned > TimeSpan.FromDays(1))
                {
                    var cutoff = now.AddDays(-365);
                    await db.Visits.Where(v => v.LastSeenAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                    await db.ServerRuns.Where(r => r.LastAliveAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                    await db.VisitStatsPauses.Where(p => p.EndedAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                    cleaned = now;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(ex, "Recording the server run failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { }
        }
        // Stopping (restart / shutdown): note the last moment it was running.
        if (runId is null) return;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            await db.ServerRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.LastAliveAt, now));
        }
        catch { /* shutting down */ }
    }
}
