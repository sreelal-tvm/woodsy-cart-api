using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Email;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Infrastructure.Social;

public record SocialReplyDto(int Id, string Author, string Text, DateTime PostedAt, bool FromUs);
public record SocialCommentDto(int Id, int PostId, int ProductId, string ProductName, string? Image, SocialPlatform Platform, string? Permalink,
    string Author, string Text, DateTime PostedAt, bool Unread, List<SocialReplyDto> Replies);
public record SocialReplyRequest(string Text);
public record SocialPlatformTotals(SocialPlatform Platform, int Posts, int? Views, int? Reach, int Likes, int Comments, int? Shares, int? Saves);
public record SocialTopPost(int Id, int ProductId, string ProductName, string? Image, SocialPlatform Platform, string? Permalink, DateTime? PublishedAt, SocialStats Stats);
public record SocialPerformance(int Days, List<SocialPlatformTotals> Platforms, List<SocialTopPost> Top, List<string> Issues, DateTime? LastRead);

/// <summary>
/// How live posts are doing (views, reach, likes, comments, shares, saves) and the comments on them, read from Instagram,
/// Facebook and Threads by the worker: recent posts often, older ones less often, posts older than 90 days not at all.
/// New comments show in Admin → Social media → Comments and are emailed to the admins (at most every 30 minutes);
/// replies are sent from Admin as the account.
/// </summary>
public class SocialEngagement(AppDbContext db, SocialService social, MetaClient meta, ThreadsClient threads, EmailSender email, ILogger<SocialEngagement> logger)
{
    private const int MaxAgeDays = 90, CallsPerRun = 30;
    private const string Meta = "Meta", Threads = "Threads";

    // ---------- Background work (SocialWorker, every 10 minutes) ----------

    public async Task RefreshAsync(CancellationToken ct)
    {
        var c = await social.ConfigAsync();
        var metaOn = SocialService.IsConnected(c);
        var threadsOn = SocialService.ThreadsConnected(c);
        if (!metaOn && !threadsOn) return;
        var now = DateTime.UtcNow;
        var posts = await db.SocialPosts.Where(s => SocialService.Live.Contains(s.Status) && s.ExternalId != null && s.PublishedAt > now.AddDays(-MaxAgeDays))
            .ToListAsync(ct);
        posts = posts.Where(s => SocialService.IsThreads(s.Platform) ? threadsOn : metaOn).ToList();
        var issues = Issues(c);

        foreach (var s in posts.Where(s => StatsDue(s, now)).OrderBy(s => s.StatsAt ?? DateTime.MinValue).Take(CallsPerRun))
        {
            try
            {
                var st = await StatsAsync(s, c, ct);
                s.Views = st.Views; s.Reach = st.Reach; s.Likes = st.Likes; s.Comments = st.Comments; s.Shares = st.Shares; s.Saves = st.Saves;
                s.StatsAt = now;
                if (st.NeedsPermission) issues[Group(s.Platform) + ":stats"] = StatsIssue(s.Platform);
            }
            catch (MetaException ex) when (ex.Code == 100) { s.StatsAt = now; }   // deleted in the app: try again later
            catch (Exception ex) when (ex is MetaException or HttpRequestException)
            {
                logger.LogWarning("Reading {Platform} numbers for post {Post} failed: {Message}", s.Platform, s.Id, ex.Message);
                if (ex is MetaException { TokenInvalid: true }) break;
            }
        }
        await db.SaveChangesAsync(ct);

        foreach (var s in posts.Where(s => CommentsDue(s, now) && !issues.ContainsKey(Group(s.Platform) + ":comments"))
                     .OrderBy(s => s.CommentsCheckedAt ?? DateTime.MinValue).Take(CallsPerRun))
        {
            try
            {
                await StoreCommentsAsync(s, c, await FetchCommentsAsync(s, c, ct), ct);
                s.CommentsCheckedAt = now;
                await db.SaveChangesAsync(ct);
            }
            catch (MetaException ex) when (ex.IsPermission) { issues[Group(s.Platform) + ":comments"] = CommentsIssue(s.Platform); }
            catch (MetaException ex) when (ex.Code == 100) { s.CommentsCheckedAt = now; await db.SaveChangesAsync(ct); }
            catch (Exception ex) when (ex is MetaException or HttpRequestException)
            {
                logger.LogWarning("Reading {Platform} comments for post {Post} failed: {Message}", s.Platform, s.Id, ex.Message);
                if (ex is MetaException { TokenInvalid: true }) break;
            }
        }
        c.EngagementIssues = issues.Count == 0 ? null : JsonSerializer.Serialize(issues);
        await db.SaveChangesAsync(ct);
        await EmailNewCommentsAsync(c, ct);
    }

    /// <summary>Numbers move fast in the first day, then settle.</summary>
    private static bool StatsDue(SocialPost s, DateTime now)
    {
        var age = now - s.PublishedAt!.Value;
        var every = age < TimeSpan.FromDays(1) ? TimeSpan.FromHours(1) : age < TimeSpan.FromDays(7) ? TimeSpan.FromHours(6) : TimeSpan.FromHours(24);
        return s.StatsAt is null || now - s.StatsAt >= every;
    }

    /// <summary>Comments come mostly in the first days: every 10 minutes for 2 days, hourly for 2 weeks, then every 6 hours.</summary>
    private static bool CommentsDue(SocialPost s, DateTime now)
    {
        var age = now - s.PublishedAt!.Value;
        var every = age < TimeSpan.FromDays(2) ? TimeSpan.FromMinutes(9) : age < TimeSpan.FromDays(14) ? TimeSpan.FromHours(1) : TimeSpan.FromHours(6);
        return s.CommentsCheckedAt is null || now - s.CommentsCheckedAt >= every;
    }

    private Task<PostStats> StatsAsync(SocialPost s, SocialConfig c, CancellationToken ct) => s.Platform switch
    {
        SocialPlatform.Instagram or SocialPlatform.InstagramReel => meta.InstagramStatsAsync(s.ExternalId!, social.Token(c), ct),
        SocialPlatform.Facebook or SocialPlatform.FacebookReel => meta.FacebookStatsAsync(s.ExternalId!, s.Platform == SocialPlatform.FacebookReel, social.Token(c), ct),
        _ => threads.StatsAsync(s.ExternalId!, social.ThreadsToken(c), ct)
    };

    private Task<List<FetchedComment>> FetchCommentsAsync(SocialPost s, SocialConfig c, CancellationToken ct) => s.Platform switch
    {
        SocialPlatform.Instagram or SocialPlatform.InstagramReel => meta.InstagramCommentsAsync(s.ExternalId!, social.Token(c), ct),
        SocialPlatform.Facebook or SocialPlatform.FacebookReel => meta.FacebookCommentsAsync(s.ExternalId!, social.Token(c), ct),
        _ => threads.RepliesAsync(s.ExternalId!, social.ThreadsToken(c), ct)
    };

    /// <summary>Adds comments we haven't seen. A new comment from someone else marks its conversation unread. On a post's first
    /// check, comments older than 3 days count as already read (no flood of old comments).</summary>
    private async Task StoreCommentsAsync(SocialPost s, SocialConfig c, List<FetchedComment> fetched, CancellationToken ct)
    {
        if (fetched.Count == 0) return;
        var known = await db.SocialComments.Where(x => x.SocialPostId == s.Id).ToListAsync(ct);
        var byId = known.ToDictionary(x => x.ExternalId);
        var parentOf = fetched.ToDictionary(f => f.Id, f => f.ParentId);
        var first = s.CommentsCheckedAt is null;
        var now = DateTime.UtcNow;
        // Oldest first, so a reply's conversation exists before the reply
        foreach (var f in fetched.OrderBy(f => f.At))
        {
            if (byId.ContainsKey(f.Id)) continue;
            var mine = f.Mine ?? IsOurs(s.Platform, f, c);
            // Threads replies can be nested: the conversation is the top reply they lead back to
            var rootId = f.ParentId;
            for (var hops = 0; rootId is not null && parentOf.GetValueOrDefault(rootId) is { } up && hops < 20; hops++) rootId = up;
            var root = rootId is null ? null : byId.GetValueOrDefault(rootId);
            if (rootId is not null && root is null) continue;   // its conversation isn't visible to us
            var x = new SocialComment
            {
                SocialPostId = s.Id, Platform = s.Platform, ExternalId = f.Id, Parent = root, Author = Cut(f.Author, 100), Text = Cut(f.Text, 2200),
                PostedAt = f.At, FromUs = mine, LastActivityAt = f.At
            };
            if (root is null) x.ReadAt = mine || (first && f.At < now.AddDays(-3)) ? now : null;
            else
            {
                if (f.At > root.LastActivityAt) root.LastActivityAt = f.At;
                if (!mine && !(first && f.At < now.AddDays(-3))) root.ReadAt = null;
            }
            db.SocialComments.Add(x);
            byId[f.Id] = x;
        }
    }

    private static bool IsOurs(SocialPlatform p, FetchedComment f, SocialConfig c) => p switch
    {
        SocialPlatform.Facebook or SocialPlatform.FacebookReel => f.AuthorId is not null && f.AuthorId == c.PageId,
        SocialPlatform.Instagram or SocialPlatform.InstagramReel => string.Equals(f.Author, c.InstagramUsername, StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(f.Author, c.ThreadsUsername, StringComparison.OrdinalIgnoreCase)
    };

    /// <summary>One email with the comments that arrived since the last email (at most every 30 minutes).</summary>
    private async Task EmailNewCommentsAsync(SocialConfig c, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (c.CommentsEmailedAt > now.AddMinutes(-30)) return;
        var since = c.CommentsEmailedAt ?? now.AddHours(-1);
        var fresh = await db.SocialComments.AsNoTracking()
            .Where(x => !x.FromUs && x.CreatedAt > since && (x.Parent == null ? x.ReadAt == null : x.Parent.ReadAt == null))
            .OrderBy(x => x.PostedAt).Select(x => new { x.Platform, x.Author, x.Text, Product = x.Post!.Product!.Name }).ToListAsync(ct);
        if (fresh.Count == 0) return;
        c.CommentsEmailedAt = now;
        await db.SaveChangesAsync(ct);
        var items = string.Join("", fresh.Take(10).Select(x =>
            $"<p><b>{Enc(x.Author)}</b> on {Enc(PlatformName(x.Platform))} · {Enc(x.Product)}<br>{Enc(x.Text.Length > 300 ? x.Text[..300] + "…" : x.Text)}</p>"));
        if (fresh.Count > 10) items += $"<p>…and {fresh.Count - 10} more.</p>";
        var subject = fresh.Count == 1 ? $"New comment from {fresh[0].Author}" : $"{fresh.Count} new comments on social media";
        try { await email.AdminsAsync(EmailEvent.AdminSocialComments, subject, subject, items, ("Reply in Admin", email.AdminUrl + "/social?tab=comments")); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not email the new comments"); }
    }

    // ---------- Admin ----------

    public Task<int> UnreadAsync() => db.SocialComments.CountAsync(x => x.ParentId == null && x.ReadAt == null);

    /// <summary>Conversations, newest activity first. <paramref name="account"/>: instagram, facebook or threads (reels included).</summary>
    public async Task<PagedResult<SocialCommentDto>> ListAsync(bool unreadOnly, string? account, int page, int pageSize)
    {
        var q = db.SocialComments.AsNoTracking().Where(x => x.ParentId == null && !x.FromUs);
        if (unreadOnly) q = q.Where(x => x.ReadAt == null);
        SocialPlatform[]? kinds = account?.ToLowerInvariant() switch
        {
            "instagram" => [SocialPlatform.Instagram, SocialPlatform.InstagramReel],
            "facebook" => [SocialPlatform.Facebook, SocialPlatform.FacebookReel],
            "threads" => [SocialPlatform.Threads, SocialPlatform.ThreadsVideo],
            _ => null
        };
        if (kinds is not null) q = q.Where(x => kinds.Contains(x.Platform));
        var total = await q.CountAsync();
        pageSize = Math.Clamp(pageSize, 1, 50);
        var tops = await q.OrderByDescending(x => x.LastActivityAt).Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                C = x, x.Post!.ProductId, x.Post.Permalink, Name = x.Post.Product!.Name,
                Image = x.Post.Product.Images.OrderBy(i => i.SortOrder).Select(i => i.LocalThumbnailPath ?? i.LocalPath).FirstOrDefault()
            }).ToListAsync();
        var ids = tops.Select(t => t.C.Id).ToList();
        var replies = await db.SocialComments.AsNoTracking().Where(x => x.ParentId != null && ids.Contains(x.ParentId.Value))
            .OrderBy(x => x.PostedAt).ToListAsync();
        return new PagedResult<SocialCommentDto>
        {
            Items = tops.Select(t => new SocialCommentDto(t.C.Id, t.C.SocialPostId, t.ProductId, t.Name, t.Image, t.C.Platform, t.Permalink,
                t.C.Author, t.C.Text, t.C.PostedAt, t.C.ReadAt is null,
                replies.Where(r => r.ParentId == t.C.Id).Select(r => new SocialReplyDto(r.Id, r.Author, r.Text, r.PostedAt, r.FromUs)).ToList())).ToList(),
            Total = total, Page = page, PageSize = pageSize
        };
    }

    public async Task MarkReadAsync(int id, bool read = true)
    {
        var x = await db.SocialComments.FirstOrDefaultAsync(c => c.Id == id && c.ParentId == null) ?? throw new AppException("Comment not found", 404);
        x.ReadAt = read ? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
    }

    public async Task<int> MarkAllReadAsync() =>
        await db.SocialComments.Where(x => x.ParentId == null && x.ReadAt == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.ReadAt, DateTime.UtcNow));

    /// <summary>Replies under the conversation as the account, and marks it read.</summary>
    public async Task<SocialReplyDto> ReplyAsync(int id, string text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) throw new AppException("Write a reply first.");
        var top = await db.SocialComments.Include(x => x.Post).FirstOrDefaultAsync(x => x.Id == id && x.ParentId == null) ?? throw new AppException("Comment not found", 404);
        if (SocialService.IsThreads(top.Platform) && System.Text.Encoding.UTF8.GetByteCount(text) > SocialCaption.ThreadsMaxLength)
            throw new AppException($"Threads allows {SocialCaption.ThreadsMaxLength} characters. Shorten the reply.");
        if (text.Length > 2200) throw new AppException("Replies can be at most 2,200 characters.");
        var c = await social.ConfigAsync();
        string newId;
        try
        {
            newId = top.Platform switch
            {
                SocialPlatform.Instagram or SocialPlatform.InstagramReel when SocialService.IsConnected(c) => await meta.ReplyInstagramAsync(top.ExternalId, social.Token(c), text),
                SocialPlatform.Facebook or SocialPlatform.FacebookReel when SocialService.IsConnected(c) => await meta.ReplyFacebookAsync(top.ExternalId, social.Token(c), text),
                SocialPlatform.Threads or SocialPlatform.ThreadsVideo when SocialService.ThreadsConnected(c) => await threads.ReplyAsync(c.ThreadsUserId!, social.ThreadsToken(c), top.ExternalId, text),
                _ => throw new AppException($"Connect {PlatformName(top.Platform)} in Settings first.")
            };
        }
        catch (MetaException ex) when (ex.IsPermission)
        {
            throw new AppException(SocialService.IsThreads(top.Platform)
                ? "Threads needs the threads_manage_replies permission to reply. Add it to the Threads use case, then connect Threads again."
                : top.Platform is SocialPlatform.Instagram or SocialPlatform.InstagramReel
                    ? "Instagram needs the instagram_manage_comments permission to reply. Add it to the Meta app, then reconnect in Settings with a new token."
                    : "Facebook needs the pages_manage_engagement permission to reply. Add it to the Meta app, then reconnect in Settings with a new token.");
        }
        catch (MetaException ex) { throw new AppException($"{PlatformName(top.Platform)} refused the reply: {ex.Message}"); }
        var now = DateTime.UtcNow;
        var reply = new SocialComment
        {
            SocialPostId = top.SocialPostId, Platform = top.Platform, ExternalId = newId, ParentId = top.Id,
            Author = Cut(OurName(top.Platform, c), 100), Text = Cut(text, 2200), PostedAt = now, FromUs = true, LastActivityAt = now
        };
        db.SocialComments.Add(reply);
        top.ReadAt = now; top.LastActivityAt = now;
        await db.SaveChangesAsync();
        return new SocialReplyDto(reply.Id, reply.Author, reply.Text, reply.PostedAt, true);
    }

    /// <summary>Totals per platform for posts published in the last <paramref name="days"/> days, and the best posts.</summary>
    public async Task<SocialPerformance> PerformanceAsync(int days)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);
        var posts = await db.SocialPosts.AsNoTracking().Where(s => SocialService.Live.Contains(s.Status) && s.PublishedAt >= since)
            .Select(s => new
            {
                Post = s, s.Product!.Name,
                Image = s.Product.Images.OrderBy(i => i.SortOrder).Select(i => i.LocalThumbnailPath ?? i.LocalPath).FirstOrDefault()
            }).ToListAsync();
        int? Sum(IEnumerable<int?> v) { var l = v.Where(x => x is not null).ToList(); return l.Count == 0 ? null : l.Sum(); }
        var platforms = posts.GroupBy(p => p.Post.Platform).OrderBy(g => g.Key).Select(g => new SocialPlatformTotals(g.Key, g.Count(),
            Sum(g.Select(p => p.Post.Views)), Sum(g.Select(p => p.Post.Reach)), g.Sum(p => p.Post.Likes ?? 0), g.Sum(p => p.Post.Comments ?? 0),
            Sum(g.Select(p => p.Post.Shares)), Sum(g.Select(p => p.Post.Saves)))).ToList();
        // Best = most views; platforms without views are ranked by likes + comments
        var top = posts.Where(p => p.Post.StatsAt is not null)
            .OrderByDescending(p => p.Post.Views ?? 0).ThenByDescending(p => (p.Post.Likes ?? 0) + (p.Post.Comments ?? 0)).Take(10)
            .Select(p => new SocialTopPost(p.Post.Id, p.Post.ProductId, p.Name, p.Image, p.Post.Platform, p.Post.Permalink, p.Post.PublishedAt, SocialStats.Of(p.Post)!))
            .ToList();
        var c = await social.ConfigAsync();
        return new SocialPerformance(days, platforms, top, Issues(c).Values.Distinct().ToList(), posts.Max(p => p.Post.StatsAt));
    }

    public async Task<List<string>> IssuesAsync() => Issues(await social.ConfigAsync()).Values.Distinct().ToList();

    // ---------- Permission problems ----------

    private static Dictionary<string, string> Issues(SocialConfig c)
    {
        if (string.IsNullOrEmpty(c.EngagementIssues)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(c.EngagementIssues) ?? []; } catch (JsonException) { return []; }
    }

    /// <summary>Forgets the problems of one account (Meta or Threads) after it was connected again.</summary>
    public static string? ClearIssues(string? json, string group)
    {
        if (string.IsNullOrEmpty(json)) return null;
        Dictionary<string, string> d;
        try { d = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; } catch (JsonException) { return null; }
        foreach (var k in d.Keys.Where(k => k.StartsWith(group + ":")).ToList()) d.Remove(k);
        return d.Count == 0 ? null : JsonSerializer.Serialize(d);
    }

    private static string Group(SocialPlatform p) => SocialService.IsThreads(p) ? Threads : Meta;

    private static string StatsIssue(SocialPlatform p) => SocialService.IsThreads(p)
        ? "Threads numbers need the threads_manage_insights permission: add it to the Threads use case, then connect Threads again."
        : "Views and reach need the instagram_manage_insights and read_insights permissions: add them to the Meta app, then reconnect Instagram and Facebook with a new token.";

    private static string CommentsIssue(SocialPlatform p) => SocialService.IsThreads(p)
        ? "Threads replies need the threads_read_replies and threads_manage_replies permissions: add them to the Threads use case, then connect Threads again."
        : "Comments need the instagram_manage_comments, pages_read_user_content and pages_manage_engagement permissions: add them to the Meta app, then reconnect Instagram and Facebook with a new token.";

    // ---------- Helpers ----------

    private static string OurName(SocialPlatform p, SocialConfig c) => p switch
    {
        SocialPlatform.Facebook or SocialPlatform.FacebookReel => c.PageName ?? "WoodsyCart",
        SocialPlatform.Instagram or SocialPlatform.InstagramReel => c.InstagramUsername ?? "WoodsyCart",
        _ => c.ThreadsUsername ?? "WoodsyCart"
    };

    private static string PlatformName(SocialPlatform p) => p switch
    {
        SocialPlatform.InstagramReel => "Instagram Reel", SocialPlatform.FacebookReel => "Facebook Reel", SocialPlatform.ThreadsVideo => "Threads video",
        _ => p.ToString()
    };

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
