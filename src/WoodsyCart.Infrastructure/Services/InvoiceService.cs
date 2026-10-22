using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;

namespace WoodsyCart.Infrastructure.Services;

public class InvoiceService(AppDbContext db, FileStorage storage)
{
    public const string ShippingSac = "996511";

    public async Task<CustomerInvoice> GenerateAsync(int orderId, DateTime? invoiceDate = null)
    {
        var existing = await db.CustomerInvoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.OrderId == orderId && !i.IsCancelled);
        if (existing is not null) return existing;

        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId) ?? throw new AppException("Order not found", 404);
        if (order.Status == Domain.OrderStatus.Cancelled) throw new AppException("Cannot invoice a cancelled order.");
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();

        var date = invoiceDate ?? DateTime.UtcNow;
        var fy = FinancialYear.For(date);
        var seq = (await db.CustomerInvoices.Where(i => i.FinancialYear == fy).MaxAsync(i => (int?)i.Sequence) ?? 0) + 1;
        var interState = !string.Equals(order.ShipState.Trim(), s.State.Trim(), StringComparison.OrdinalIgnoreCase);

        var inv = new CustomerInvoice
        {
            InvoiceNumber = $"{s.InvoicePrefix}/{fy}/{seq:0000}",
            FinancialYear = fy, Sequence = seq, OrderId = order.Id, InvoiceDate = date,
            IsTaxInvoice = s.GstRegistered,
            BillToName = order.ShipName,
            BillToAddress = string.Join(", ", new[] { order.ShipLine1, order.ShipLine2, order.ShipLandmark, order.ShipCity, $"{order.ShipState} - {order.ShipPincode}" }
                .Where(x => !string.IsNullOrWhiteSpace(x))),
            BillToPhone = order.ShipPhone, BillToEmail = order.ShipEmail, BillToGstin = order.CustomerGstin,
            PlaceOfSupply = order.ShipState, IsInterState = interState,
            Discount = order.Discount, ShippingFee = order.ShippingFee, OtherCharges = order.CodFee
        };

        var sub = order.Items.Sum(i => i.LineTotal);
        foreach (var item in order.Items)
        {
            var discountShare = sub == 0 ? 0 : Money.R2(order.Discount * item.LineTotal / sub);
            var net = item.LineTotal - discountShare;
            var (taxable, tax) = s.GstRegistered ? Money.SplitInclusive(net, item.GstRate) : (net, 0m);
            inv.Lines.Add(new CustomerInvoiceLine
            {
                Description = item.VariantName is null ? item.ProductName : $"{item.ProductName} ({item.VariantName})",
                HsnCode = item.HsnCode, Quantity = item.Quantity, UnitPrice = item.UnitPrice, Discount = discountShare,
                TaxableValue = taxable, GstRate = s.GstRegistered ? item.GstRate : 0, TaxAmount = tax, Total = net
            });
        }
        var charges = order.ShippingFee + order.CodFee;
        if (charges > 0)
        {
            var (taxable, tax) = s.GstRegistered ? Money.SplitInclusive(charges, 18) : (charges, 0m);
            inv.Lines.Add(new CustomerInvoiceLine
            {
                Description = "Delivery & handling charges", HsnCode = ShippingSac, Quantity = 1, UnitPrice = charges,
                TaxableValue = taxable, GstRate = s.GstRegistered ? 18 : 0, TaxAmount = tax, Total = charges
            });
        }

        inv.TaxableAmount = inv.Lines.Sum(l => l.TaxableValue);
        var totalTax = inv.Lines.Sum(l => l.TaxAmount);
        if (interState) inv.Igst = totalTax;
        else
        {
            inv.Cgst = Money.R2(totalTax / 2);
            inv.Sgst = totalTax - inv.Cgst;
        }
        var exact = inv.TaxableAmount + totalTax;
        inv.Total = Math.Round(exact, 0, MidpointRounding.AwayFromZero);
        inv.RoundOff = inv.Total - exact;

        db.CustomerInvoices.Add(inv);
        await db.SaveChangesAsync();
        return inv;
    }

    public async Task<byte[]> RenderPdfAsync(int invoiceId)
    {
        var inv = await db.CustomerInvoices.AsNoTracking().Include(i => i.Lines).Include(i => i.Order)
            .FirstOrDefaultAsync(i => i.Id == invoiceId) ?? throw new AppException("Invoice not found", 404);
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var logo = ResolveLogo(s.LogoUrl);
        return Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(28);
            page.DefaultTextStyle(t => t.FontSize(9).FontFamily(Fonts.Arial));

            page.Header().Element(h => BusinessHeader(h, s, logo, inv.IsTaxInvoice ? "TAX INVOICE" : "BILL OF SUPPLY", inv.IsCancelled));

            page.Content().PaddingVertical(10).Column(col =>
            {
                col.Spacing(10);
                col.Item().Row(r =>
                {
                    r.RelativeItem().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(b =>
                    {
                        b.Item().Text("Bill To / Ship To").SemiBold().FontColor(Colors.Grey.Darken1);
                        b.Item().Text(inv.BillToName).Bold().FontSize(10);
                        b.Item().Text(inv.BillToAddress);
                        if (!string.IsNullOrEmpty(inv.BillToPhone)) b.Item().Text($"Phone: {inv.BillToPhone}");
                        if (!string.IsNullOrEmpty(inv.BillToGstin)) b.Item().Text($"GSTIN: {inv.BillToGstin}");
                    });
                    r.ConstantItem(10);
                    r.ConstantItem(210).Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(b =>
                    {
                        Kv(b, "Invoice No", inv.InvoiceNumber);
                        Kv(b, "Invoice Date", FinancialYear.ToIst(inv.InvoiceDate).ToString("dd MMM yyyy"));
                        Kv(b, "Order No", inv.Order!.OrderNumber);
                        Kv(b, "Order Date", FinancialYear.ToIst(inv.Order.CreatedAt).ToString("dd MMM yyyy"));
                        Kv(b, "Place of Supply", inv.PlaceOfSupply);
                        Kv(b, "Payment", inv.Order.PaymentMethod switch { Domain.PaymentMethod.CashOnDelivery => "Cash on delivery", Domain.PaymentMethod.Razorpay => "Online (Razorpay)", Domain.PaymentMethod.BankTransfer => "Bank transfer", _ => "UPI" });
                    });
                });

                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd =>
                    {
                        cd.ConstantColumn(20); cd.RelativeColumn(4); cd.ConstantColumn(44); cd.ConstantColumn(26);
                        cd.ConstantColumn(58); cd.ConstantColumn(46); cd.ConstantColumn(60);
                        if (inv.IsTaxInvoice) { cd.ConstantColumn(30); cd.ConstantColumn(52); }
                        cd.ConstantColumn(62);
                    });
                    t.Header(h =>
                    {
                        var heads = new List<string> { "#", "Item", "HSN/SAC", "Qty", "Rate", "Disc.", "Taxable" };
                        if (inv.IsTaxInvoice) heads.AddRange(["GST%", "GST"]);
                        heads.Add("Amount");
                        foreach (var head in heads)
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).AlignCenter().Text(head).SemiBold();
                    });
                    var n = 0;
                    foreach (var l in inv.Lines)
                    {
                        Cell(t, (++n).ToString(), true);
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(l.Description);
                        Cell(t, l.HsnCode ?? "", true);
                        Cell(t, l.Quantity.ToString(), true);
                        Cell(t, l.UnitPrice.ToString("N2"));
                        Cell(t, l.Discount.ToString("N2"));
                        Cell(t, l.TaxableValue.ToString("N2"));
                        if (inv.IsTaxInvoice) { Cell(t, $"{l.GstRate:0.#}", true); Cell(t, l.TaxAmount.ToString("N2")); }
                        Cell(t, l.Total.ToString("N2"));
                    }
                });

                col.Item().Row(r =>
                {
                    r.RelativeItem().Column(b =>
                    {
                        b.Spacing(4);
                        b.Item().Text("Amount in words").SemiBold().FontColor(Colors.Grey.Darken1);
                        b.Item().Text(Money.ToWords(inv.Total));
                        if (!string.IsNullOrWhiteSpace(s.BankDetails))
                        {
                            b.Item().PaddingTop(6).Text("Bank details").SemiBold().FontColor(Colors.Grey.Darken1);
                            b.Item().Text(s.BankDetails);
                        }
                        if (!string.IsNullOrWhiteSpace(s.UpiId)) b.Item().Text($"UPI: {s.UpiId}");
                    });
                    r.ConstantItem(20);
                    r.ConstantItem(220).Column(b =>
                    {
                        Kv(b, "Taxable value", inv.TaxableAmount.ToString("N2"));
                        if (inv.IsTaxInvoice)
                        {
                            if (inv.IsInterState) Kv(b, "IGST", inv.Igst.ToString("N2"));
                            else { Kv(b, "CGST", inv.Cgst.ToString("N2")); Kv(b, "SGST", inv.Sgst.ToString("N2")); }
                        }
                        if (inv.RoundOff != 0) Kv(b, "Round off", inv.RoundOff.ToString("N2"));
                        b.Item().PaddingTop(4).BorderTop(1).BorderColor(Colors.Grey.Darken1).PaddingTop(4).Row(x =>
                        {
                            x.RelativeItem().Text("Grand Total").Bold().FontSize(11);
                            x.RelativeItem().AlignRight().Text($"₹ {inv.Total:N2}").Bold().FontSize(11);
                        });
                        if (inv.Order!.AmountPaid > 0) Kv(b, "Amount paid", inv.Order.AmountPaid.ToString("N2"));
                    });
                });

                if (!string.IsNullOrWhiteSpace(s.InvoiceTerms))
                    col.Item().Column(b =>
                    {
                        b.Item().Text("Terms & conditions").SemiBold().FontColor(Colors.Grey.Darken1);
                        b.Item().Text(s.InvoiceTerms).FontSize(8);
                    });

                col.Item().AlignRight().PaddingTop(20).Column(b =>
                {
                    b.Item().AlignRight().Text($"For {s.LegalName ?? s.BusinessName}").SemiBold();
                    b.Item().PaddingTop(28).AlignRight().Text("Authorised Signatory").FontColor(Colors.Grey.Darken1);
                });
            });

            page.Footer().AlignCenter().Text(t =>
            {
                t.Span("This is a computer-generated document. ").FontSize(8).FontColor(Colors.Grey.Medium);
                if (!inv.IsTaxInvoice) t.Span("Supplier not registered under GST — no tax charged. ").FontSize(8).FontColor(Colors.Grey.Medium);
                t.Span("Thank you for shopping with us!").FontSize(8).FontColor(Colors.Grey.Medium);
            });
        })).GeneratePdf();
    }

    public async Task<byte[]> RenderPurchaseOrderPdfAsync(int purchaseOrderId)
    {
        var po = await db.PurchaseOrders.AsNoTracking().Include(p => p.Vendor).Include(p => p.Order).ThenInclude(o => o!.Items)
            .FirstOrDefaultAsync(p => p.Id == purchaseOrderId) ?? throw new AppException("Purchase order not found", 404);
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var o = po.Order!;
        return Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(28);
            page.DefaultTextStyle(t => t.FontSize(9).FontFamily(Fonts.Arial));
            page.Header().Element(h => BusinessHeader(h, s, ResolveLogo(s.LogoUrl), "PURCHASE ORDER", false));
            page.Content().PaddingVertical(10).Column(col =>
            {
                col.Spacing(10);
                col.Item().Row(r =>
                {
                    r.RelativeItem().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(b =>
                    {
                        b.Item().Text("Vendor").SemiBold().FontColor(Colors.Grey.Darken1);
                        b.Item().Text(po.Vendor!.Name).Bold();
                        if (po.Vendor.Address is not null) b.Item().Text(po.Vendor.Address);
                        if (po.Vendor.Gstin is not null) b.Item().Text($"GSTIN: {po.Vendor.Gstin}");
                    });
                    r.ConstantItem(10);
                    r.RelativeItem().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(b =>
                    {
                        b.Item().Text("Deliver to (drop-ship)").SemiBold().FontColor(Colors.Grey.Darken1);
                        b.Item().Text(o.ShipName).Bold();
                        b.Item().Text(string.Join(", ", new[] { o.ShipLine1, o.ShipLine2, o.ShipLandmark, o.ShipCity, $"{o.ShipState} - {o.ShipPincode}" }.Where(x => !string.IsNullOrWhiteSpace(x))));
                        b.Item().Text($"Phone: {o.ShipPhone}");
                    });
                    r.ConstantItem(10);
                    r.ConstantItem(160).Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(b =>
                    {
                        Kv(b, "PO No", po.PoNumber);
                        Kv(b, "Date", FinancialYear.ToIst(po.CreatedAt).ToString("dd MMM yyyy"));
                        Kv(b, "Our Ref", o.OrderNumber);
                    });
                });
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(20); cd.RelativeColumn(); cd.ConstantColumn(64); cd.ConstantColumn(26); cd.ConstantColumn(62); cd.ConstantColumn(40); cd.ConstantColumn(66); cd.ConstantColumn(70); });
                    t.Header(h =>
                    {
                        foreach (var head in new[] { "#", "Item", "Vendor item", "Qty", "Unit price", "GST", "Unit incl. GST", "Amount" })
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text(head).SemiBold();
                    });
                    var n = 0;
                    foreach (var i in o.Items)
                    {
                        Cell(t, (++n).ToString(), true);
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(
                            (i.VendorVariantName ?? i.VariantName) is { } vn ? $"{i.ProductName} — {vn}" : i.ProductName);
                        Cell(t, i.VendorProductId?.ToString() ?? "-", true);
                        Cell(t, i.Quantity.ToString(), true);
                        Cell(t, (i.UnitVendorPrice > 0 ? i.UnitVendorPrice : i.UnitVendorCost).ToString("N2"));
                        Cell(t, i.UnitVendorPrice > 0 && i.UnitVendorCost > i.UnitVendorPrice ? $"{i.GstRate:0.#}%" : "incl.", true);
                        Cell(t, i.UnitVendorCost.ToString("N2"));
                        Cell(t, (i.UnitVendorCost * i.Quantity).ToString("N2"));
                    }
                });
                col.Item().AlignRight().Text($"Total payable incl. GST: ₹ {po.ExpectedCost:N2}").Bold();
                col.Item().Text("Please deliver directly to the customer above. Do not include WoodsyCart's selling price or any price tags in the package. " +
                                "Share the dispatch details and your invoice once shipped.").FontColor(Colors.Grey.Darken2);
                if (!string.IsNullOrWhiteSpace(po.Notes)) col.Item().Text($"Notes: {po.Notes}");
            });
        })).GeneratePdf();
    }

    private byte[]? ResolveLogo(string? logoUrl)
    {
        if (string.IsNullOrEmpty(logoUrl) || !logoUrl.StartsWith("/media/")) return null;
        var path = storage.FullPath(logoUrl);
        return File.Exists(path) && !path.EndsWith(".webp") ? File.ReadAllBytes(path) : null;
    }

    private static void BusinessHeader(IContainer h, BusinessSettings s, byte[]? logo, string title, bool cancelled)
    {
        h.BorderBottom(1).BorderColor(Colors.Grey.Lighten1).PaddingBottom(8).Row(r =>
        {
            if (logo is not null) r.ConstantItem(60).Height(50).Image(logo).FitArea();
            r.RelativeItem().PaddingLeft(logo is null ? 0 : 8).Column(c =>
            {
                c.Item().Text(s.LegalName ?? s.BusinessName).Bold().FontSize(16).FontColor("#9A5226");
                c.Item().Text(string.Join(", ", new[] { s.AddressLine1, s.AddressLine2, s.City, $"{s.State} - {s.Pincode}" }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)));
                c.Item().Text(string.Join("   ", new[] { string.IsNullOrWhiteSpace(s.Phone) ? null : $"Phone: {s.Phone}", $"Email: {s.Email}", s.Website }.Where(x => !string.IsNullOrWhiteSpace(x))));
                if (s.GstRegistered && !string.IsNullOrEmpty(s.Gstin)) c.Item().Text($"GSTIN: {s.Gstin}   State code: {s.StateCode}").SemiBold();
            });
            r.ConstantItem(170).AlignRight().Column(c =>
            {
                c.Item().AlignRight().Text(title).Bold().FontSize(14);
                if (cancelled) c.Item().AlignRight().Text("CANCELLED").Bold().FontColor(Colors.Red.Medium);
                else c.Item().AlignRight().Text("Original for recipient").FontSize(8).FontColor(Colors.Grey.Medium);
            });
        });
    }

    private static void Kv(ColumnDescriptor b, string k, string v) =>
        b.Item().Row(r =>
        {
            r.RelativeItem().Text(k).FontColor(Colors.Grey.Darken1);
            r.RelativeItem().AlignRight().Text(v).SemiBold();
        });

    private static void Cell(TableDescriptor t, string text, bool center = false)
    {
        var c = t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4);
        if (center) c.AlignCenter().Text(text); else c.AlignRight().Text(text);
    }
}
