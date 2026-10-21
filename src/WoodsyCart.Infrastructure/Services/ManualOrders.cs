using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Contracts;
using WoodsyCart.Infrastructure.Payments;

namespace WoodsyCart.Infrastructure.Services;

public record ManualOrderLine(int ProductId, int? VariantId, int Quantity, decimal? UnitPrice);

/// <summary>How a manual order is paid: already in full, an advance now, a payment link for later, or cash on delivery.</summary>
public enum ManualPaymentMode { Paid = 0, Advance = 1, PaymentLink = 2, CashOnDelivery = 3 }

public record ManualOrderRequest(
    int? CustomerId, string Name, string Phone, string? Email, AddressRequest Address,
    SalesChannel Channel, string? ChannelReference, List<ManualOrderLine> Items,
    decimal Discount, decimal? ShippingFee, string? CustomerNotes, string? AdminNotes, DateTime? ExpectedDeliveryDate, string? CustomerGstin,
    ManualPaymentMode PaymentMode, PaymentMethod PaymentMethod, decimal? AmountPaid, string? PaymentReference, DateTime? PaidAt);

public record ManualQuoteLine(int ProductId, int? VariantId, string Name, string? VariantName, string? ImageUrl, int Quantity,
    decimal ListPrice, decimal RegularPrice, string? OfferName, decimal UnitPrice, decimal UnitCost, decimal LineTotal, bool InStock, string? Problem);

public record ManualQuote(List<ManualQuoteLine> Lines, decimal SubTotal, decimal Discount, decimal ShippingFee, decimal SuggestedShippingFee,
    decimal Total, decimal Cost, decimal Margin, int DeliveryDays, PincodeCheckDto? Pincode, List<string> Warnings);

public record ManualOrderResult(int Id, string OrderNumber, string CustomerLink, string WhatsAppText, string WhatsAppUrl);

public record CustomerLookup(int Id, string Name, string? Phone, string? Email, int Orders, AddressRequest? LastAddress);

public partial class OrderService
{
    /// <summary>Customers without an email (common on Instagram / WhatsApp) get a placeholder login email that is never shown or mailed.</summary>
    public const string NoEmailDomain = "@no-email.woodsycart";
    public static bool IsPlaceholderEmail(string? e) => e?.EndsWith(NoEmailDomain, StringComparison.OrdinalIgnoreCase) == true;
    private static string? RealEmail(string? e) => string.IsNullOrWhiteSpace(e) || IsPlaceholderEmail(e) ? null : e;

    /// <summary>Find existing customers by name, mobile or email (for the New order form).</summary>
    public async Task<List<CustomerLookup>> FindCustomersAsync(string q)
    {
        q = q.Trim();
        if (q.Length < 2) return [];
        var digits = new string(q.Where(char.IsDigit).ToArray());
        var phone = digits.Length >= 10 ? digits[^10..] : null;
        var users = await db.Users.AsNoTracking().Where(u => u.Role == UserRole.Customer &&
                (u.FullName.Contains(q) || u.Email.Contains(q) || (u.Phone != null && (phone != null ? u.Phone.EndsWith(phone) : u.Phone.Contains(q)))))
            .OrderByDescending(u => u.CreatedAt).Take(10)
            .Select(u => new { u.Id, u.FullName, u.Phone, u.Email, Orders = db.Orders.Count(o => o.UserId == u.Id) }).ToListAsync();
        var ids = users.Select(u => u.Id).ToList();
        var lastOrders = (await db.Orders.AsNoTracking().Where(o => ids.Contains(o.UserId)).OrderByDescending(o => o.CreatedAt)
                .Select(o => new { o.UserId, A = new AddressRequest("Home", o.ShipName, o.ShipPhone, o.ShipLine1, o.ShipLine2, o.ShipLandmark, o.ShipCity, o.ShipState, o.ShipPincode, false, o.ShipLatitude, o.ShipLongitude) })
                .ToListAsync())
            .GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => g.First().A);
        var saved = (await db.Addresses.AsNoTracking().Where(a => ids.Contains(a.UserId)).OrderByDescending(a => a.IsDefault).ToListAsync())
            .GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.First());
        return users.Select(u => new CustomerLookup(u.Id, u.FullName, u.Phone, RealEmail(u.Email), u.Orders,
            lastOrders.GetValueOrDefault(u.Id) ?? (saved.GetValueOrDefault(u.Id) is { } a
                ? new AddressRequest(a.Label, a.FullName, a.Phone, a.Line1, a.Line2, a.Landmark, a.City, a.State, a.Pincode, false, a.Latitude, a.Longitude) : null))).ToList();
    }

    /// <summary>Prices, cost and delivery for the New order form, before anything is saved.</summary>
    public async Task<ManualQuote> QuoteManualAsync(List<ManualOrderLine> items, decimal discount, decimal? shippingFee, string? pincode)
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var resolved = await ResolveAsync(items.Select(i => new CartLineRequest(i.ProductId, i.VariantId, i.Quantity)).ToList());
        var warnings = new List<string>();
        var lines = new List<ManualQuoteLine>();
        foreach (var l in resolved)
        {
            var req = items.FirstOrDefault(i => i.ProductId == l.Product.Id && i.VariantId == l.Variant?.Id);
            var price = req?.UnitPrice is > 0 ? Math.Round(req.UnitPrice.Value, 2) : l.UnitPrice;
            var inStock = l.Problem != "Out of stock";
            var problem = l.Problem == "Out of stock" ? null : l.Problem; // you may still sell it; the vendor confirms
            lines.Add(new ManualQuoteLine(l.Product.Id, l.Variant?.Id, l.Product.Name, l.Variant?.Name,
                CatalogService.ThumbUrl(l.Product.Images.Where(i => i.MediaType == MediaType.Image).OrderBy(i => i.SortOrder).FirstOrDefault()),
                l.Quantity, l.UnitPrice, l.RegularPrice, req?.UnitPrice is > 0 ? null : l.Offer?.Name, price, l.UnitCost, price * l.Quantity, inStock, problem));
            if (price < l.UnitCost) warnings.Add($"{l.Product.Name}: the price is below your cost (₹{l.UnitCost:N0}) — you lose money on it.");
            if (!inStock) warnings.Add($"{l.Product.Name} is marked out of stock at the vendor — check with them first.");
            if (problem is not null) warnings.Add($"{l.Product.Name}: {problem}");
        }
        var sub = lines.Sum(l => l.LineTotal);
        discount = Math.Clamp(Math.Round(discount, 2), 0, sub);
        PincodeCheckDto? pin = string.IsNullOrWhiteSpace(pincode) ? null : await CheckPincodeAsync(pincode);
        if (pin is { Serviceable: false }) warnings.Add(pin.Message);
        var suggested = sub == 0 ? 0 : (s.FreeShippingThreshold > 0 && sub - discount >= s.FreeShippingThreshold ? 0 : s.ShippingFee) + (pin?.ExtraCharge ?? 0);
        var shipping = Math.Max(0, shippingFee ?? suggested);
        var cost = lines.Sum(l => l.UnitCost * l.Quantity);
        var total = sub - discount + shipping;
        var days = Math.Max(pin?.DeliveryDays ?? s.DefaultDeliveryDays, resolved.Count == 0 ? 0 : resolved.Max(l => l.Product.DeliveryDays));
        if (total - cost < 0 && sub > 0) warnings.Add("This order makes a loss overall.");
        return new ManualQuote(lines, sub, discount, shipping, suggested, total, cost, total - cost, days, pin, warnings);
    }

    /// <summary>
    /// An order taken on Instagram, Facebook, WhatsApp, the phone or in person. It becomes a normal order (same numbering,
    /// purchase order, invoice and P&amp;L) with its sales channel recorded.
    /// </summary>
    public async Task<ManualOrderResult> CreateManualOrderAsync(ManualOrderRequest r, string staff)
    {
        if (r.Items.Count == 0 || r.Items.All(i => i.Quantity <= 0)) throw new AppException("Add at least one product.");
        if (r.Channel == SalesChannel.Website) throw new AppException("Choose where the order came from (Instagram, WhatsApp, …).");
        if (r.Items.Any(i => i.UnitPrice is <= 0)) throw new AppException("Prices must be more than ₹0.");
        // The delivery name and phone default to the customer's own.
        var address = MapAddress(new Address(), r.Address with
        {
            FullName = string.IsNullOrWhiteSpace(r.Address.FullName) ? r.Name : r.Address.FullName,
            Phone = string.IsNullOrWhiteSpace(r.Address.Phone) ? r.Phone : r.Address.Phone
        });
        var phone = Mobile.Normalise(string.IsNullOrWhiteSpace(r.Phone) ? r.Address.Phone : r.Phone);
        var mail = string.IsNullOrWhiteSpace(r.Email) ? null : r.Email.Trim().ToLowerInvariant();
        if (mail is not null && (!mail.Contains('@') || mail.Length > 200)) throw new AppException("Enter a valid email, or leave it empty.");

        var user = await FindOrCreateCustomerAsync(r.CustomerId, r.Name.Trim(), phone, mail);
        var quote = await QuoteManualAsync(r.Items, r.Discount, r.ShippingFee, address.Pincode);
        if (quote.Lines.Any(l => l.Problem is not null)) throw new AppException(quote.Lines.First(l => l.Problem is not null).Problem!);
        if (quote.Pincode is { Serviceable: false }) throw new AppException(quote.Pincode.Message);

        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var resolved = await ResolveAsync(r.Items.Select(i => new CartLineRequest(i.ProductId, i.VariantId, i.Quantity)).ToList());
        var method = r.PaymentMode == ManualPaymentMode.CashOnDelivery ? PaymentMethod.CashOnDelivery : r.PaymentMethod;
        var provider = r.PaymentMode switch
        {
            ManualPaymentMode.CashOnDelivery => PaymentProviders.CashOnDelivery,
            ManualPaymentMode.PaymentLink => await LinkProviderAsync(),
            _ => null
        };
        var lines = quote.Lines;
        var tax = ComputeTax(lines.Select(l => (l.LineTotal, resolved.First(x => x.Product.Id == l.ProductId && x.Variant?.Id == l.VariantId).Product.GstRate)).ToList(),
            quote.Discount, quote.ShippingFee, s.GstRegistered);

        var order = new Order
        {
            OrderNumber = await NextOrderNumberAsync(),
            UserId = user.Id, Channel = r.Channel, ChannelReference = string.IsNullOrWhiteSpace(r.ChannelReference) ? null : r.ChannelReference.Trim(),
            CreatedByStaff = staff,
            PaymentMethod = method, PaymentProvider = provider,
            ShipName = address.FullName, ShipPhone = address.Phone, ShipEmail = mail,
            ShipLine1 = address.Line1, ShipLine2 = address.Line2, ShipLandmark = address.Landmark,
            ShipCity = address.City, ShipState = address.State, ShipPincode = address.Pincode,
            ShipLatitude = address.Latitude, ShipLongitude = address.Longitude,
            CustomerGstin = string.IsNullOrWhiteSpace(r.CustomerGstin) ? null : r.CustomerGstin.Trim().ToUpper(),
            SubTotal = quote.SubTotal, Discount = quote.Discount, ShippingFee = quote.ShippingFee, Total = quote.Total, TaxAmount = tax,
            CustomerNotes = r.CustomerNotes?.Trim(), AdminNotes = r.AdminNotes?.Trim(),
            ExpectedDeliveryDate = r.ExpectedDeliveryDate is { } d ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : DateTime.UtcNow.Date.AddDays(quote.DeliveryDays + 2),
            EstimatedCost = quote.Cost
        };
        foreach (var l in resolved)
        {
            var q = lines.First(x => x.ProductId == l.Product.Id && x.VariantId == l.Variant?.Id);
            var offer = q.OfferName is null ? null : l.Offer;
            order.Items.Add(new OrderItem
            {
                ProductId = l.Product.Id, VariantId = l.Variant?.Id, ProductName = l.Product.Name, VariantName = l.Variant?.Name,
                VendorVariantName = l.Variant is null ? null : ProductOptions.VendorFacingName(l.Variant, l.Product),
                ImageUrl = q.ImageUrl, HsnCode = l.Product.HsnCode, GstRate = l.Product.GstRate,
                Quantity = l.Quantity, UnitMrp = l.UnitMrp, UnitPrice = q.UnitPrice, LineTotal = q.LineTotal,
                UnitRegularPrice = offer is null ? q.UnitPrice : l.RegularPrice, OfferId = offer?.Id, OfferName = offer?.Name,
                UnitVendorPrice = l.UnitVendorPrice, UnitVendorCost = l.UnitCost, VendorProductId = l.Product.VendorProductId, VendorUrl = l.Product.VendorUrl
            });
        }
        order.OfferDiscount = order.Items.Sum(i => (i.UnitRegularPrice - i.UnitPrice) * i.Quantity);
        order.History.Add(new OrderStatusHistory
        {
            Status = OrderStatus.Pending, CreatedBy = staff,
            Note = $"Order placed via {ChannelName(r.Channel)}{(order.ChannelReference is null ? "" : $" ({order.ChannelReference})")}"
        });
        if (provider == PaymentProviders.DirectUpi) order.UpiAmount = await UniqueUpiAmountAsync(order.Total);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // Money already received (in full, or an advance)
        if (r.PaymentMode is ManualPaymentMode.Paid or ManualPaymentMode.Advance)
        {
            var amount = r.PaymentMode == ManualPaymentMode.Paid ? order.Total : r.AmountPaid ?? 0;
            if (amount <= 0) throw new AppException("Enter the advance amount received.");
            if (amount > order.Total) throw new AppException("The amount received is more than the order total.");
            var payment = new Payment
            {
                OrderId = order.Id, Method = method, Amount = amount, IsSuccessful = true,
                Reference = string.IsNullOrWhiteSpace(r.PaymentReference) ? null : r.PaymentReference.Trim(),
                PaidAt = r.PaidAt is { } p ? DateTime.SpecifyKind(p, DateTimeKind.Utc) : DateTime.UtcNow,
                Notes = r.PaymentMode == ManualPaymentMode.Advance ? "Advance" : $"Paid via {ChannelName(r.Channel)} order"
            };
            db.Payments.Add(payment);
            order.AmountPaid = amount;
            order.PaymentStatus = amount >= order.Total - 1m ? PaymentStatus.Paid : PaymentStatus.PartiallyPaid;
            db.OrderStatusHistory.Add(new OrderStatusHistory
            {
                OrderId = order.Id, Status = OrderStatus.Pending, VisibleToCustomer = false, CreatedBy = staff,
                Note = $"₹{amount:N2} received ({method}){(payment.Reference is null ? "" : $", ref {payment.Reference}")}"
            });
            if (order.PaymentStatus == PaymentStatus.Paid)
            {
                order.Status = OrderStatus.Confirmed;
                db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = order.Id, Status = OrderStatus.Confirmed, Note = "Payment received — thank you!", CreatedBy = staff });
            }
        }
        await db.SaveChangesAsync();
        if (RealEmail(order.ShipEmail) is not null) await NotifyOrderPlacedAsync(order);

        var (text, link) = await CustomerMessageAsync(order);
        return new ManualOrderResult(order.Id, order.OrderNumber, link, text, WhatsAppLink(order.ShipPhone, text));
    }

    /// <summary>Payment links use Direct UPI when it's set up (no fee, confirmed automatically), else bank transfer.</summary>
    private async Task<string?> LinkProviderAsync()
    {
        var (upiOn, _, upi) = await payments.GetAsync(PaymentProviders.DirectUpi);
        if (upiOn && !string.IsNullOrWhiteSpace(upi.GetValueOrDefault("upiId"))) return PaymentProviders.DirectUpi;
        var (bankOn, _, bank) = await payments.GetAsync(PaymentProviders.BankTransfer);
        if (bankOn && !string.IsNullOrWhiteSpace(bank.GetValueOrDefault("accountNumber"))) return PaymentProviders.BankTransfer;
        throw new AppException("To send a payment link, set up Direct UPI (or bank transfer) in Payment integrations first.");
    }

    private async Task<AppUser> FindOrCreateCustomerAsync(int? customerId, string name, string phone, string? mail)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new AppException("Enter the customer's name.");
        AppUser? user = customerId is > 0 ? await db.Users.FirstOrDefaultAsync(u => u.Id == customerId && u.Role == UserRole.Customer) : null;
        user ??= await db.Users.FirstOrDefaultAsync(u => u.Role == UserRole.Customer && u.Phone == phone);
        if (user is null && mail is not null) user = await db.Users.FirstOrDefaultAsync(u => u.Email == mail);
        if (user is { Role: not UserRole.Customer }) throw new AppException("This email belongs to a team member — use the customer's own email.");
        if (user is null)
        {
            user = new AppUser { FullName = name, Phone = phone, Email = mail ?? phone + NoEmailDomain, IsGuest = true, Role = UserRole.Customer };
            db.Users.Add(user);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(user.Phone)) user.Phone = phone;
            if (mail is not null && IsPlaceholderEmail(user.Email) && !await db.Users.AnyAsync(u => u.Email == mail)) user.Email = mail;
            if (user.IsGuest) user.FullName = name;
        }
        await db.SaveChangesAsync();
        return user;
    }

    public static string ChannelName(SalesChannel c) => c switch
    {
        SalesChannel.WhatsApp => "WhatsApp", SalesChannel.Phone => "phone call", SalesChannel.WalkIn => "walk-in / showroom", _ => c.ToString()
    };

    /// <summary>Ready-to-send WhatsApp message for the customer: order summary, delivery date, payment link and tracking link.</summary>
    public async Task<(string text, string link)> CustomerMessageAsync(Order o)
    {
        var storefront = (email.StorefrontUrl ?? "").TrimEnd('/');
        var link = $"{storefront}/order/{o.OrderNumber}?token={o.AccessToken}";
        var items = o.Items.Count > 0 ? o.Items : await db.OrderItems.Where(i => i.OrderId == o.Id).ToListAsync();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Hi {o.ShipName.Split(' ')[0]}, thank you for your order with WoodsyCart!");
        sb.AppendLine();
        sb.AppendLine($"*Order {o.OrderNumber}*");
        foreach (var i in items) sb.AppendLine($"• {i.ProductName}{(i.VariantName is null ? "" : $" ({i.VariantName})")} × {i.Quantity} — ₹{i.LineTotal:N0}");
        if (o.Discount > 0) sb.AppendLine($"Discount: −₹{o.Discount:N0}");
        sb.AppendLine($"Delivery: {(o.ShippingFee > 0 ? $"₹{o.ShippingFee:N0}" : "Free")}");
        sb.AppendLine($"*Total: ₹{o.Total:N0}*");
        if (o.ExpectedDeliveryDate is { } d) sb.AppendLine($"Expected delivery: {d:ddd, d MMM}");
        sb.AppendLine();
        var due = o.Total - o.AmountPaid;
        if (o.PaymentStatus == PaymentStatus.Paid) sb.AppendLine("Payment received — thank you!");
        else if (o.PaymentMethod == PaymentMethod.CashOnDelivery) sb.AppendLine($"Please keep ₹{due:N0} ready at delivery.");
        else if (o.PaymentProvider is PaymentProviders.DirectUpi or PaymentProviders.BankTransfer)
            sb.AppendLine($"{(o.AmountPaid > 0 ? $"₹{o.AmountPaid:N0} received. " : "")}Please pay ₹{(o.UpiAmount ?? o.Total) - o.AmountPaid:N2} here (UPI — exact amount, so it's confirmed automatically):");
        else if (due > 0) sb.AppendLine($"₹{o.AmountPaid:N0} received; balance ₹{due:N0}.");
        sb.AppendLine($"View, pay and track your order: {link}");
        sb.AppendLine();
        sb.AppendLine("— Team WoodsyCart");
        return (sb.ToString(), link);
    }

    /// <summary>Google Maps link to the delivery point (exact pin when picked on the map, otherwise an address search). Free — no API call.</summary>
    public static string MapLink(Order o) => o.ShipLatitude is { } lat && o.ShipLongitude is { } lng
        ? FormattableString.Invariant($"https://www.google.com/maps/search/?api=1&query={lat},{lng}")
        : "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(string.Join(", ", new[] { o.ShipLine1, o.ShipLine2, o.ShipCity, o.ShipPincode }.Where(x => !string.IsNullOrWhiteSpace(x))));

    public static string WhatsAppLink(string phone, string text)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 10) digits = "91" + digits;
        return $"https://wa.me/{digits}?text={Uri.EscapeDataString(text)}";
    }
}
