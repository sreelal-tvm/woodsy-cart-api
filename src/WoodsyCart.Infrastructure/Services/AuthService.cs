using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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
public record UpdateProfileRequest(string FullName, string? Phone);
public record UserDto(int Id, string FullName, string Email, string? Phone, string Role);
public record AuthResponse(string Token, DateTime ExpiresAt, UserDto User);

public class AuthService(AppDbContext db, IOptions<JwtOptions> jwtOptions)
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
        await db.SaveChangesAsync();
    }

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
