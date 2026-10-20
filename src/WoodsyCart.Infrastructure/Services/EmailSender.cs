using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WoodsyCart.Infrastructure.Services;

public class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "no-reply@woodsycart.in";
    public string? AdminNotifyEmail { get; set; }
    /// <summary>Public storefront address used for links in emails, e.g. https://woodsycart.in</summary>
    public string? StorefrontUrl { get; set; }
}

/// <summary>Sends mail when SMTP is configured; otherwise logs and does nothing.</summary>
public class EmailSender(IOptions<SmtpOptions> options, ILogger<EmailSender> logger)
{
    private readonly SmtpOptions _o = options.Value;

    public string? AdminEmail => _o.AdminNotifyEmail;
    public string? StorefrontUrl => _o.StorefrontUrl;

    public async Task SendAsync(string? to, string subject, string html)
    {
        if (string.IsNullOrWhiteSpace(to)) return;
        if (string.IsNullOrWhiteSpace(_o.Host))
        {
            logger.LogInformation("SMTP not configured; skipped email '{Subject}' to {To}", subject, to);
            return;
        }
        try
        {
            using var client = new SmtpClient(_o.Host, _o.Port) { EnableSsl = true, Credentials = new NetworkCredential(_o.User, _o.Password) };
            using var msg = new MailMessage(_o.From, to, subject, html) { IsBodyHtml = true };
            await client.SendMailAsync(msg);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Email send failed to {To}", to);
        }
    }
}
