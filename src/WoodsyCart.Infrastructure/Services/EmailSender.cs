using System.Net;
using Microsoft.Extensions.Options;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Email;

namespace WoodsyCart.Infrastructure.Services;

/// <summary>Fallback mail settings from appsettings "Smtp" (Admin → Email &amp; notifications takes priority), and the public links used in emails.</summary>
public class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "no-reply@woodsycart.com";
    public string? AdminNotifyEmail { get; set; }
    /// <summary>Public storefront address used for links in emails, e.g. https://woodsycart.com</summary>
    public string? StorefrontUrl { get; set; }
    /// <summary>Admin app address used for links in admin emails, e.g. https://admin.woodsycart.com</summary>
    public string? AdminUrl { get; set; }
    /// <summary>Public API address, e.g. https://api.woodsycart.com (photo links in sitemap.xml)</summary>
    public string? ApiUrl { get; set; }
}

/// <summary>
/// Queues branded emails to customers and to the admin team. Each notification can be switched off in Admin;
/// sending happens in the background (<see cref="EmailWorker"/>) and every attempt is logged.
/// </summary>
public class EmailSender(EmailQueue queue, EmailSettingsService settings, IOptions<SmtpOptions> options)
{
    public string? StorefrontUrl => options.Value.StorefrontUrl?.TrimEnd('/');
    public string? AdminUrl => options.Value.AdminUrl?.TrimEnd('/');

    /// <summary>Link to a customer's order that works without signing in (guest checkout, manual orders).</summary>
    public string OrderLink(Order o) => $"{StorefrontUrl}/order/{o.OrderNumber}?token={o.AccessToken}";
    public string AdminOrderLink(Order o) => $"{AdminUrl}/orders/{o.Id}";

    public async Task CustomerAsync(EmailEvent ev, string? to, string subject, string heading, string bodyHtml,
        (string Text, string Url)? button = null, EmailAttachment? attachment = null, int? orderId = null)
    {
        if (string.IsNullOrWhiteSpace(to) || OrderService.IsPlaceholderEmail(to) || !await IsOnAsync(ev)) return;
        queue.Enqueue(new EmailMessage(ev, [to.Trim()], subject, EmailTemplates.Layout(heading, bodyHtml, button), attachment, orderId));
    }

    public async Task AdminsAsync(EmailEvent ev, string subject, string heading, string bodyHtml, (string Text, string Url)? button = null, int? orderId = null)
    {
        var s = await settings.GetRuntimeAsync();
        if (s.AdminEmails.Count == 0 || !await IsOnAsync(ev)) return;
        queue.Enqueue(new EmailMessage(ev, s.AdminEmails, subject, EmailTemplates.Layout(heading, bodyHtml, button, admin: true), null, orderId));
    }

    /// <summary>Sends straight away (not queued) so Admin can see the result of the test.</summary>
    public async Task<(EmailLogStatus status, string? error)> SendTestAsync(string to)
    {
        var s = await settings.GetRuntimeAsync();
        if (s.Server is null) return (EmailLogStatus.Skipped, "Save the mail server settings and switch email on first.");
        var html = EmailTemplates.Layout("Your email is working 🎉",
            "<p>This is a test email from <b>WoodsyCart Admin</b>. If you can read this, order and payment emails will reach your customers and your team.</p>" +
            $"<p style=\"color:#6B5A4E;font-size:13px\">Sent through {WebUtility.HtmlEncode(s.Server.Host)}:{s.Server.Port} as {WebUtility.HtmlEncode(s.Server.FromEmail)}.</p>", null);
        return await EmailWorker.SendAsync(s.Server, new EmailMessage(EmailEvent.Test, [to], "WoodsyCart test email", html));
    }

    /// <summary>A mail server is saved and email is switched on.</summary>
    public async Task<bool> CanSendAsync() => (await settings.GetRuntimeAsync()).Server is not null;

    private async Task<bool> IsOnAsync(EmailEvent ev) =>
        ev is EmailEvent.Test or EmailEvent.PasswordReset or EmailEvent.PasswordChanged || !(await settings.GetRuntimeAsync()).Events.TryGetValue(ev.ToString(), out var on) || on;
}
