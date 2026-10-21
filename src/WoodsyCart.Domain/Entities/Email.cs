using WoodsyCart.Domain.Common;

namespace WoodsyCart.Domain.Entities;

/// <summary>The mail server used for customer and admin emails (one row, edited in Admin → Email &amp; notifications).</summary>
public class EmailConfig : BaseEntity
{
    public bool Enabled { get; set; }
    /// <summary>Preset the settings came from (gmail, zoho, office365, brevo, ses, hostinger, custom) — for the Admin form only.</summary>
    public string? Provider { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    /// <summary>Encrypt the connection with STARTTLS (port 587). Off only for servers on port 25 without encryption.</summary>
    public bool UseStartTls { get; set; } = true;
    public string? Username { get; set; }
    /// <summary>Encrypted with ASP.NET Data Protection; never sent back to the browser.</summary>
    public string? PasswordProtected { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }
    public string? ReplyTo { get; set; }
    /// <summary>Who receives admin notifications, separated by commas.</summary>
    public string? AdminEmails { get; set; }
    /// <summary>JSON map of notification key → on/off. Missing keys are on.</summary>
    public string? EventsJson { get; set; }
}

public enum EmailLogStatus { Sent = 0, Failed = 1, Skipped = 2 }

/// <summary>Every email the app tried to send, for the "Sent emails" list.</summary>
public class EmailLog : BaseEntity
{
    public string Event { get; set; } = "";
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public EmailLogStatus Status { get; set; }
    public string? Error { get; set; }
    public int? OrderId { get; set; }
}
