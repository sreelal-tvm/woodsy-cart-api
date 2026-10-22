using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;

namespace WoodsyCart.Infrastructure.Services;

public class JwtOptions
{
    public string Issuer { get; set; } = "WoodsyCart";
    public string Audience { get; set; } = "WoodsyCart";
    public string Key { get; set; } = "";
    public int CustomerTokenDays { get; set; } = 30;
    public int AdminTokenHours { get; set; } = 12;
}

public record RegisterRequest(string FullName, string Email, string? Phone, string Password);
public record LoginRequest(string Email, string Password);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record ForgotPasswordRequest(string Email);
public record AdminSetPasswordRequest(string NewPassword, bool NotifyCustomer);
public record CustomerPasswordStatus(bool HasPassword, DateTime? ChangedAt, string? ChangedBy, DateTime? ResetLinkSentAt, bool ResetLinkActive, bool CanEmail);
public record ResetPasswordRequest(string Email, string Token, string NewPassword);
public record UpdateProfileRequest(string FullName, string? Phone);
public record UserDto(int Id, string FullName, string Email, string? Phone, string Role);
public record AuthResponse(string Token, DateTime ExpiresAt, UserDto User);

public class AuthService(AppDbContext db, IOptions<JwtOptions> jwtOptions, EmailSender mailer)
{
    private readonly PasswordHasher<AppUser> _hasher = new();
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public static UserDto ToDto(AppUser u) => new(u.Id, u.FullName, u.Email, u.Phone, u.Role.ToString());

    public string HashPassword(AppUser user, string password) => _hasher.HashPassword(user, password);

    public async Task<AuthResponse> RegisterAsync(RegisterRequest req)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(req.FullName)) throw new AppException("Name is required.");
        if (!email.Contains('@')) throw new AppException("A valid email is required.");
        ValidatePassword(req.Password);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is not null && !user.IsGuest) throw new AppException("An account with this email already exists. Please sign in.");

        // A guest who checked out before becomes a full customer and keeps their previous orders.
        if (user is null)
        {
            user = new AppUser { Role = UserRole.Customer, Email = email };
            db.Users.Add(user);
        }
        user.FullName = req.FullName.Trim();
        user.Phone = Mobile.NormaliseOptional(req.Phone) ?? user.Phone;
        user.IsGuest = false;
        user.PasswordHash = HashPassword(user, req.Password);
        await db.SaveChangesAsync();
        await mailer.CustomerAsync(Email.EmailEvent.AccountCreated, user.Email, "Welcome to WoodsyCart", $"Welcome, {user.FullName.Split(' ')[0]}!",
            "<p>Your WoodsyCart account is ready. You can now track your orders, save addresses and keep a wishlist.</p>" +
            "<p>Factory-direct furniture, delivered and assembled free across Bengaluru.</p>", ("Start shopping", mailer.StorefrontUrl + "/shop"));
        return Issue(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest req, bool adminOnly)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null || !user.IsActive || user.IsGuest ||
            _hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password) == PasswordVerificationResult.Failed)
            throw new AppException("Invalid email or password.", 401);
        if (adminOnly && user.Role == UserRole.Customer) throw new AppException("You do not have admin access.", 403);

        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Issue(user);
    }

    public async Task ChangePasswordAsync(int userId, ChangePasswordRequest req)
    {
        var user = await db.Users.FindAsync(userId) ?? throw new AppException("User not found", 404);
        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword) == PasswordVerificationResult.Failed)
            throw new AppException("Current password is incorrect.");
        ValidatePassword(req.NewPassword);
        user.PasswordHash = HashPassword(user, req.NewPassword);
        user.PasswordChangedAt = DateTime.UtcNow;
        user.PasswordChangedBy = "Customer";
        await db.SaveChangesAsync();
    }

    // ---------------- Admin → Customers: help a customer who can't sign in

    private async Task<AppUser> CustomerAsync(int userId) =>
        await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.Role == UserRole.Customer) ?? throw new AppException("Customer not found", 404);

    public async Task<CustomerPasswordStatus> PasswordStatusAsync(int userId)
    {
        var u = await CustomerAsync(userId);
        return new CustomerPasswordStatus(!u.IsGuest && !string.IsNullOrEmpty(u.PasswordHash), u.PasswordChangedAt, u.PasswordChangedBy,
            u.PasswordResetRequestedAt, u.PasswordResetExpiresAt > DateTime.UtcNow, await mailer.CanSendAsync() && !OrderService.IsPlaceholderEmail(u.Email));
    }

    /// <summary>Emails the customer a reset link (same link as "Forgot password").</summary>
    public async Task<CustomerPasswordStatus> AdminSendResetLinkAsync(int userId)
    {
        var u = await CustomerAsync(userId);
        if (!u.IsActive) throw new AppException("This customer is blocked. Unblock them first.");
        if (OrderService.IsPlaceholderEmail(u.Email)) throw new AppException("This customer has no real email address. Set a new password instead and share it on WhatsApp.");
        if (!await mailer.CanSendAsync()) throw new AppException("Email isn't set up yet (Admin → Email & notifications). Set a new password instead and share it with the customer.");
        await SendResetLinkAsync(u);
        return await PasswordStatusAsync(userId);
    }

    /// <summary>Sets a password chosen by the admin (to share with the customer by phone / WhatsApp). Old reset links stop working.</summary>
    public async Task<CustomerPasswordStatus> AdminSetPasswordAsync(int userId, AdminSetPasswordRequest req, string byName)
    {
        var u = await CustomerAsync(userId);
        ValidatePassword(req.NewPassword);
        u.PasswordHash = HashPassword(u, req.NewPassword);
        u.PasswordResetTokenHash = null;
        u.PasswordResetExpiresAt = null;
        u.IsGuest = false;
        u.PasswordChangedAt = DateTime.UtcNow;
        u.PasswordChangedBy = byName;
        await db.SaveChangesAsync();
        if (req.NotifyCustomer)
            await mailer.CustomerAsync(Email.EmailEvent.PasswordChanged, u.Email, "Your WoodsyCart password was changed", "Password changed",
                $"<p>Hi {Email.EmailTemplates.E(u.FullName.Split(' ')[0])},</p><p>As you asked, our team has set a new password for your WoodsyCart account " +
                $"(<b>{Email.EmailTemplates.E(u.Email)}</b>). We'll share it with you directly — please change it after you sign in (Account → Profile).</p>" +
                "<p style=\"color:#6B5A4E;font-size:13px\">Didn't ask for this? Reply to this email or message us on WhatsApp straight away.</p>",
                ("Sign in", mailer.StorefrontUrl + "/login"));
        return await PasswordStatusAsync(userId);
    }

    public const int ResetLinkMinutes = 60;

    /// <summary>
    /// Emails a one-time reset link (valid <see cref="ResetLinkMinutes"/> minutes). Says nothing about whether the email is
    /// registered, and sends at most one link every 2 minutes per account. Guest-checkout customers can use it to set a password.
    /// </summary>
    public async Task ForgotPasswordAsync(ForgotPasswordRequest req)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        if (!email.Contains('@')) throw new AppException("Enter the email address you use with WoodsyCart.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null || !user.IsActive || OrderService.IsPlaceholderEmail(user.Email)) return;
        if (user.PasswordResetRequestedAt > DateTime.UtcNow.AddMinutes(-2)) return;
        await SendResetLinkAsync(user);
    }

    private async Task SendResetLinkAsync(AppUser user)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        user.PasswordResetTokenHash = Sha256(token);
        user.PasswordResetExpiresAt = DateTime.UtcNow.AddMinutes(ResetLinkMinutes);
        user.PasswordResetRequestedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var link = $"{mailer.StorefrontUrl}/reset-password?email={Uri.EscapeDataString(user.Email)}&token={token}";
        var first = Email.EmailTemplates.E(user.FullName.Split(' ')[0]);
        await mailer.CustomerAsync(Email.EmailEvent.PasswordReset, user.Email, "Reset your WoodsyCart password",
            user.IsGuest ? "Set a password for your account" : "Reset your password",
            $"<p>Hi {first},</p><p>We received a request to {(user.IsGuest ? "set a password for" : "reset the password of")} your WoodsyCart account " +
            $"(<b>{Email.EmailTemplates.E(user.Email)}</b>). Tap the button below to choose a new password.</p>" +
            Email.EmailTemplates.Info($"This link works once and expires in <b>{ResetLinkMinutes} minutes</b>.") +
            "<p style=\"color:#6B5A4E;font-size:13px\">Didn't ask for this? You can ignore this email — your password stays the same.</p>",
            (user.IsGuest ? "Set my password" : "Reset my password", link));
    }

    /// <summary>Checks the emailed token, sets the new password and signs the customer in.</summary>
    public async Task<AuthResponse> ResetPasswordAsync(ResetPasswordRequest req)
    {
        const string invalid = "This reset link is invalid or has expired. Please request a new one.";
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null || !user.IsActive || string.IsNullOrWhiteSpace(req.Token) || user.PasswordResetTokenHash is null ||
            user.PasswordResetExpiresAt is null || user.PasswordResetExpiresAt < DateTime.UtcNow ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(user.PasswordResetTokenHash), Encoding.ASCII.GetBytes(Sha256(req.Token.Trim()))))
            throw new AppException(invalid);
        ValidatePassword(req.NewPassword);

        user.PasswordHash = HashPassword(user, req.NewPassword);
        user.PasswordResetTokenHash = null;
        user.PasswordResetExpiresAt = null;
        user.IsGuest = false; // a guest-checkout customer now has a full account with their past orders
        user.LastLoginAt = DateTime.UtcNow;
        user.PasswordChangedAt = DateTime.UtcNow;
        user.PasswordChangedBy = "Customer";
        await db.SaveChangesAsync();

        await mailer.CustomerAsync(Email.EmailEvent.PasswordChanged, user.Email, "Your WoodsyCart password was changed", "Password changed",
            $"<p>Hi {Email.EmailTemplates.E(user.FullName.Split(' ')[0])},</p><p>The password for your WoodsyCart account was just changed.</p>" +
            "<p style=\"color:#6B5A4E;font-size:13px\">If this wasn't you, reply to this email or message us on WhatsApp straight away.</p>",
            ("Go to my account", mailer.StorefrontUrl + "/account"));
        return Issue(user);
    }

    private static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            throw new AppException("Password must be at least 8 characters.");
    }

    private AuthResponse Issue(AppUser user)
    {
        var expires = user.Role == UserRole.Customer
            ? DateTime.UtcNow.AddDays(_jwt.CustomerTokenDays)
            : DateTime.UtcNow.AddHours(_jwt.AdminTokenHours);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key));
        var token = new JwtSecurityToken(_jwt.Issuer, _jwt.Audience, claims, expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new AuthResponse(new JwtSecurityTokenHandler().WriteToken(token), expires, ToDto(user));
    }
}
