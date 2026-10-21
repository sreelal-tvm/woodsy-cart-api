using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Email;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Api.Controllers.Admin;

public record TestEmailRequest(string To);
public record EmailLogRow(int Id, DateTime CreatedAt, string Event, string To, string Subject, EmailLogStatus Status, string? Error, int? OrderId);

/// <summary>Admin → Email &amp; notifications: mail server, who gets admin emails, which notifications are on, and the sent-email log.</summary>
[Route("api/admin/email")]
public class AdminEmailController(EmailSettingsService settings, EmailSender email, AppDbContext db) : AdminControllerBase
{
    [HttpGet("settings")]
    public Task<EmailSettingsDto> Get() => settings.GetAsync();

    [HttpPut("settings")]
    public async Task<EmailSettingsDto> Save(EmailSettingsUpdate r)
    {
        await settings.SaveAsync(r);
        return await settings.GetAsync();
    }

    /// <summary>Sends a test email right away and reports the mail server's answer.</summary>
    [HttpPost("test")]
    public async Task<object> Test(TestEmailRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.To) || !r.To.Contains('@')) throw new AppException("Enter the email address to send the test to.");
        var (status, error) = await email.SendTestAsync(r.To.Trim());
        db.EmailLogs.Add(new EmailLog { Event = nameof(EmailEvent.Test), To = r.To.Trim(), Subject = "WoodsyCart test email", Status = status, Error = error });
        await db.SaveChangesAsync();
        return new { ok = status == EmailLogStatus.Sent, message = status == EmailLogStatus.Sent ? $"Test email sent to {r.To.Trim()} — check the inbox (and spam folder)." : error };
    }

    [HttpGet("log")]
    public async Task<PagedResult<EmailLogRow>> Log(EmailLogStatus? status, int page = 1)
    {
        var q = db.EmailLogs.AsNoTracking().Where(l => status == null || l.Status == status);
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(l => l.Id).Skip((Math.Max(1, page) - 1) * 30).Take(30)
            .Select(l => new EmailLogRow(l.Id, l.CreatedAt, l.Event, l.To, l.Subject, l.Status, l.Error, l.OrderId)).ToListAsync();
        return new PagedResult<EmailLogRow> { Items = items, Total = total, Page = page, PageSize = 30 };
    }
}
