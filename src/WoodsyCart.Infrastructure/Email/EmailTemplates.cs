using System.Net;
using System.Text;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;

namespace WoodsyCart.Infrastructure.Email;

/// <summary>Simple, mobile-friendly HTML emails (inline styles, as email apps require).</summary>
public static class EmailTemplates
{
    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    public static string Rs(decimal v) => "₹" + v.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-IN"));

    public static string Layout(string heading, string body, (string Text, string Url)? button, bool admin = false)
    {
        var btn = button is { } b && !string.IsNullOrWhiteSpace(b.Url)
            ? $"<p style=\"margin:24px 0 8px\"><a href=\"{E(b.Url)}\" style=\"background:#3874ff;color:#ffffff;text-decoration:none;font-weight:700;padding:12px 22px;border-radius:8px;display:inline-block\">{E(b.Text)}</a></p>"
            : "";
        var footer = admin
            ? "WoodsyCart Admin notification — change which emails you get in Admin → Email &amp; notifications."
            : "WoodsyCart · Furniture in Bengaluru, factory-direct<br/>Reply to this email or WhatsApp us if you have any questions.";
        return $"""
            <!doctype html><html><body style="margin:0;padding:0;background:#f3f4f6;font-family:Segoe UI,Arial,sans-serif;color:#1f2937">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f3f4f6;padding:24px 12px"><tr><td align="center">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:600px;background:#ffffff;border-radius:12px;overflow:hidden">
              <tr><td style="padding:20px 28px;border-bottom:1px solid #eef0f4;font-size:22px;font-weight:800;color:#0f172a">woodsy<span style="color:#3874ff">cart</span>{(admin ? " <span style=\"font-size:12px;font-weight:600;color:#6b7280\">ADMIN</span>" : "")}</td></tr>
              <tr><td style="padding:28px">
                <h1 style="margin:0 0 14px;font-size:21px;color:#0f172a">{E(heading)}</h1>
                <div style="font-size:15px;line-height:1.6">{body}</div>
                {btn}
              </td></tr>
              <tr><td style="padding:16px 28px;background:#f9fafb;color:#6b7280;font-size:12px;line-height:1.5">{footer}</td></tr>
            </table></td></tr></table></body></html>
            """;
    }

    /// <summary>Items with sizes, quantities and line totals, then the totals.</summary>
    public static string OrderTable(Order o, IEnumerable<OrderItem> items)
    {
        var sb = new StringBuilder("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;margin:14px 0;font-size:14px\">");
        foreach (var i in items)
            sb.Append($"<tr><td style=\"padding:8px 0;border-bottom:1px solid #eef0f4\"><b>{E(i.ProductName)}</b>{(i.VariantName is null ? "" : $"<br/><span style=\"color:#6b7280\">{E(i.VariantName)}</span>")}" +
                      $"<br/><span style=\"color:#6b7280\">Qty {i.Quantity}</span></td><td align=\"right\" style=\"padding:8px 0;border-bottom:1px solid #eef0f4;white-space:nowrap\">{Rs(i.LineTotal)}</td></tr>");
        void Row(string label, string value, bool bold = false) =>
            sb.Append($"<tr><td style=\"padding:4px 0;{(bold ? "font-weight:800;font-size:16px" : "color:#6b7280")}\">{label}</td><td align=\"right\" style=\"padding:4px 0;{(bold ? "font-weight:800;font-size:16px" : "")}\">{value}</td></tr>");
        if (o.Discount > 0) Row($"Discount{(o.CouponCode is null ? "" : $" ({E(o.CouponCode)})")}", "−" + Rs(o.Discount));
        Row("Delivery", o.ShippingFee > 0 ? Rs(o.ShippingFee) : "Free");
        if (o.CodFee > 0) Row("Cash on delivery fee", Rs(o.CodFee));
        Row("Total", Rs(o.Total), bold: true);
        if (o.AmountPaid > 0 && o.AmountPaid < o.Total) { Row("Paid", Rs(o.AmountPaid)); Row("Balance due", Rs(o.Total - o.AmountPaid)); }
        return sb.Append("</table>").ToString();
    }

    public static string Address(Order o) =>
        $"<p style=\"margin:8px 0;color:#374151\"><b>Delivering to</b><br/>{E(o.ShipName)} · {E(o.ShipPhone)}<br/>{E(o.ShipLine1)}{(string.IsNullOrWhiteSpace(o.ShipLine2) ? "" : ", " + E(o.ShipLine2))}<br/>{E(o.ShipCity)} – {E(o.ShipPincode)}</p>";

    public static string Delivery(Order o) => o.ExpectedDeliveryDate is { } d
        ? $"<p style=\"margin:8px 0\">Expected delivery: <b>{d:dddd, d MMMM}</b></p>" : "";

    public static string Info(string text, string color = "#eff6ff", string border = "#3874ff") =>
        $"<div style=\"background:{color};border-left:4px solid {border};padding:10px 14px;border-radius:6px;margin:12px 0\">{text}</div>";

    public static string PaymentLabel(Order o) => o.PaymentMethod switch
    {
        PaymentMethod.CashOnDelivery => "Cash on delivery", PaymentMethod.Upi => "UPI", PaymentMethod.Card => "Card",
        PaymentMethod.NetBanking => "Net banking", PaymentMethod.Wallet => "Wallet", PaymentMethod.Emi => "EMI",
        PaymentMethod.BankTransfer => "Bank transfer", _ => "Online"
    };
}
