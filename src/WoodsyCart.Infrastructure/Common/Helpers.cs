using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WoodsyCart.Infrastructure.Common;

/// <summary>Indian mobile numbers, stored as 10 digits (e.g. 9746002126).</summary>
public static class Mobile
{
    /// <summary>Accepts "+91 97460 02126", "097460-02126", "9746002126" …; throws a friendly error for anything that isn't a valid mobile number.</summary>
    public static string Normalise(string? input)
    {
        var digits = new string((input ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 12 && digits.StartsWith("91")) digits = digits[2..];
        else if (digits.Length == 11 && digits.StartsWith('0')) digits = digits[1..];
        if (digits.Length != 10 || digits[0] < '6')
            throw new AppException("Enter a valid 10-digit mobile number (for example 98765 43210).");
        return digits;
    }

    /// <summary>Same as Normalise, but an empty value stays empty.</summary>
    public static string? NormaliseOptional(string? input) => string.IsNullOrWhiteSpace(input) ? null : Normalise(input);
}

public static partial class Slug
{
    public static string Create(string text, int maxLength = 80)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        var s = NonAlnum().Replace(sb.ToString().ToLowerInvariant(), "-").Trim('-');
        if (s.Length > maxLength) s = s[..maxLength].TrimEnd('-');
        return string.IsNullOrEmpty(s) ? "item" : s;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnum();
}

public static class FinancialYear
{
    private static readonly TimeZoneInfo Ist = TryIst();

    private static TimeZoneInfo TryIst()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
    }

    public static DateTime ToIst(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Ist);

    /// <summary>Indian FY runs April–March, formatted "26-27".</summary>
    public static string For(DateTime utc)
    {
        var d = ToIst(utc);
        var start = d.Month >= 4 ? d.Year : d.Year - 1;
        return $"{start % 100:00}-{(start + 1) % 100:00}";
    }
}

public static class Money
{
    public static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Splits a GST-inclusive amount into taxable value and tax.</summary>
    public static (decimal taxable, decimal tax) SplitInclusive(decimal inclusive, decimal gstRate)
    {
        if (gstRate <= 0) return (R2(inclusive), 0);
        var taxable = R2(inclusive * 100m / (100m + gstRate));
        return (taxable, R2(inclusive - taxable));
    }

    private static readonly string[] Ones = ["", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
        "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"];
    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    /// <summary>Amount in words using the Indian numbering system (Lakh/Crore).</summary>
    public static string ToWords(decimal amount)
    {
        var rupees = (long)Math.Floor(amount);
        var paise = (int)Math.Round((amount - rupees) * 100);
        var words = rupees == 0 ? "Zero" : Words(rupees);
        var result = $"Rupees {words}";
        if (paise > 0) result += $" and {Words(paise)} Paise";
        return result + " Only";
    }

    private static string Words(long n)
    {
        if (n < 20) return Ones[n];
        if (n < 100) return (Tens[n / 10] + " " + Ones[n % 10]).Trim();
        if (n < 1000) return (Ones[n / 100] + " Hundred " + Words(n % 100)).Trim();
        if (n < 100000) return (Words(n / 1000) + " Thousand " + Words(n % 1000)).Trim();
        if (n < 10000000) return (Words(n / 100000) + " Lakh " + Words(n % 100000)).Trim();
        return (Words(n / 10000000) + " Crore " + Words(n % 10000000)).Trim();
    }
}

public class PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public class AppException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
