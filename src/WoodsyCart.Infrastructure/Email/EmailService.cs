using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Infrastructure.Email;

public record EmailAttachment(string FileName, byte[] Content, string ContentType = "application/pdf");

public record EmailMessage(EmailEvent Event, List<string> To, string Subject, string Html, EmailAttachment? Attachment = null, int? OrderId = null);

/// <summary>Mail server settings as used for sending (password decrypted).</summary>
public record MailServer(string Host, int Port, bool UseStartTls, string? Username, string? Password, string FromEmail, string? FromName, string? ReplyTo);

public record EmailSettingsDto(bool Enabled, string? Provider, string? Host, int Port, bool UseStartTls, string? Username, bool HasPassword,
    string? FromEmail, string? FromName, string? ReplyTo, List<string> AdminEmails, Dictionary<string, bool> Events, bool Configured,
    List<EmailEventInfo> EventCatalog);

public record EmailSettingsUpdate(bool Enabled, string? Provider, string? Host, int Port, bool UseStartTls, string? Username, string? Password,
    string? FromEmail, string? FromName, string? ReplyTo, List<string>? AdminEmails, Dictionary<string, bool>? Events);

/// <summary>Loads and saves the mail server settings (Admin → Email &amp; notifications). Falls back to appsettings "Smtp".</summary>
public class EmailSettingsService(AppDbContext db, IDataProtectionProvider dp, IMemoryCache cache, IOptions<SmtpOptions> fallback)
{
    private const string CacheKey = "email:settings";
    private readonly IDataProtector _protector = dp.CreateProtector("WoodsyCart.EmailSecrets.v1");

    public record Runtime(MailServer? Server, List<string> AdminEmails, Dictionary<string, bool> Events);

    public async Task<Runtime> GetRuntimeAsync() => (await cache.GetOrCreateAsync(CacheKey, async e =>
    {
        e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
        var c = await db.Set<EmailConfig>().AsNoTracking().FirstOrDefaultAsync();
        var f = fallback.Value;
        MailServer? server = null;
        if (c is { Enabled: true } && !string.IsNullOrWhiteSpace(c.Host) && !string.IsNullOrWhiteSpace(c.FromEmail))
            server = new MailServer(c.Host!, c.Port, c.UseStartTls, c.Username, Unprotect(c.PasswordProtected), c.FromEmail!, c.FromName, c.ReplyTo);
        else if (c is null && !string.IsNullOrWhiteSpace(f.Host))
            server = new MailServer(f.Host!, f.Port, true, f.User, f.Password, f.From, "WoodsyCart", null);
        var admins = SplitEmails(c?.AdminEmails ?? f.AdminNotifyEmail);
        return new Runtime(server, admins, ReadEvents(c?.EventsJson));
    }))!;

    public async Task<EmailSettingsDto> GetAsync()
    {
        var c = await db.Set<EmailConfig>().AsNoTracking().FirstOrDefaultAsync() ?? new EmailConfig { FromName = "WoodsyCart" };
        var events = ReadEvents(c.EventsJson);
        foreach (var info in EmailEvents.All) events.TryAdd(info.Key, true);
        return new EmailSettingsDto(c.Enabled, c.Provider, c.Host, c.Port, c.UseStartTls, c.Username, !string.IsNullOrEmpty(c.PasswordProtected),
            c.FromEmail, c.FromName, c.ReplyTo, SplitEmails(c.AdminEmails), events,
            (await GetRuntimeAsync()).Server is not null, EmailEvents.All.ToList());
    }

    public async Task SaveAsync(EmailSettingsUpdate r)
    {
        var c = await db.Set<EmailConfig>().FirstOrDefaultAsync();
        if (c is null) { c = new EmailConfig(); db.Add(c); }
        var admins = (r.AdminEmails ?? []).Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToList();
        foreach (var a in admins.Append(r.FromEmail?.Trim() ?? "").Where(a => a.Length > 0))
            if (!a.Contains('@') || a.Contains(' ')) throw new AppException($"\"{a}\" is not a valid email address.");
        if (r.Enabled && (string.IsNullOrWhiteSpace(r.Host) || string.IsNullOrWhiteSpace(r.FromEmail)))
            throw new AppException("Enter the mail server and the From email before switching email on.");
        if (r.Port is < 1 or > 65535) throw new AppException("Enter a valid port (usually 587).");
        c.Enabled = r.Enabled; c.Provider = r.Provider; c.Host = r.Host?.Trim(); c.Port = r.Port; c.UseStartTls = r.UseStartTls;
        c.Username = r.Username?.Trim(); c.FromEmail = r.FromEmail?.Trim(); c.FromName = r.FromName?.Trim(); c.ReplyTo = r.ReplyTo?.Trim();
        if (!string.IsNullOrEmpty(r.Password)) c.PasswordProtected = _protector.Protect(r.Password);
        c.AdminEmails = string.Join(", ", admins);
        if (r.Events is not null) c.EventsJson = JsonSerializer.Serialize(r.Events);
        await db.SaveChangesAsync();
        cache.Remove(CacheKey);
    }

    private string? Unprotect(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        try { return _protector.Unprotect(v); } catch { return null; }
    }

    public static List<string> SplitEmails(string? s) =>
        (s ?? "").Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static Dictionary<string, bool> ReadEvents(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, bool>>(json) ?? []; } catch { return []; }
    }
}

/// <summary>Emails waiting to be sent; requests never wait for the mail server.</summary>
public class EmailQueue
{
    private readonly Channel<EmailMessage> _channel = Channel.CreateUnbounded<EmailMessage>();
    public void Enqueue(EmailMessage m) => _channel.Writer.TryWrite(m);
    public IAsyncEnumerable<EmailMessage> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Sends queued emails one by one and records each attempt in the email log.</summary>
public class EmailWorker(EmailQueue queue, IServiceScopeFactory scopes, ILogger<EmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var m in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var settings = await scope.ServiceProvider.GetRequiredService<EmailSettingsService>().GetRuntimeAsync();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var (status, error) = settings.Server is null
                    ? (EmailLogStatus.Skipped, "Email is not set up — Admin → Email & notifications.")
                    : await SendAsync(settings.Server, m, stoppingToken);
                if (status == EmailLogStatus.Failed) logger.LogWarning("Email '{Subject}' to {To} failed: {Error}", m.Subject, string.Join(", ", m.To), error);
                db.Add(new EmailLog
                {
                    Event = m.Event.ToString(), To = Clip(string.Join(", ", m.To), 500), Subject = Clip(m.Subject, 500),
                    Status = status, Error = error is null ? null : Clip(error, 500), OrderId = m.OrderId
                });
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Email worker error"); }
        }
    }

    public static async Task<(EmailLogStatus, string?)> SendAsync(MailServer s, EmailMessage m, CancellationToken ct = default)
    {
        try
        {
            using var client = new SmtpClient(s.Host, s.Port) { EnableSsl = s.UseStartTls, DeliveryMethod = SmtpDeliveryMethod.Network, Timeout = 30_000 };
            if (!string.IsNullOrEmpty(s.Username)) client.Credentials = new NetworkCredential(s.Username, s.Password);
            using var msg = new MailMessage { From = new MailAddress(s.FromEmail, s.FromName ?? "WoodsyCart"), Subject = m.Subject, Body = m.Html, IsBodyHtml = true };
            foreach (var to in m.To) msg.To.Add(to);
            if (!string.IsNullOrWhiteSpace(s.ReplyTo)) msg.ReplyToList.Add(s.ReplyTo);
            if (m.Attachment is { } a) msg.Attachments.Add(new Attachment(new MemoryStream(a.Content), a.FileName, a.ContentType));
            await client.SendMailAsync(msg, ct);
            return (EmailLogStatus.Sent, null);
        }
        catch (Exception ex)
        {
            return (EmailLogStatus.Failed, Friendly(ex));
        }
    }

    /// <summary>The mail server's actual reason, in plain words where we can.</summary>
    private static string Friendly(Exception ex)
    {
        var all = ex.Message + " " + ex.InnerException?.Message;
        if (all.Contains("5.7.0") || all.Contains("5.7.8") || all.Contains("535") || all.Contains("Authentication", StringComparison.OrdinalIgnoreCase))
            return "The mail server rejected the username or password. (Gmail/Microsoft 365 need an \"app password\".) " + ex.Message;
        if (ex is SmtpException { StatusCode: SmtpStatusCode.MustIssueStartTlsFirst }) return "This server requires encryption — switch on STARTTLS (port 587).";
        if (all.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "The mail server didn't answer — check the server name and port (587). Port 465 isn't supported; use 587.";
        return all.Trim();
    }

    private static string Clip(string s, int n) => s.Length > n ? s[..n] : s;
}
