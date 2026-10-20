using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Contracts;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Payments;

namespace WoodsyCart.Infrastructure.Services;

/// <summary>Who is asking for an order: a signed-in customer, or anyone holding the order's secret access token (guest checkout).</summary>
public record OrderAccess(int? UserId, string? Token);

public class OrderService(AppDbContext db, RazorpayService razorpay, EmailSender email, PaymentConfigService payments)
{
    public const int ReturnWindowDays = 7;

    // ---------- Pricing / quote ----------

    private record ResolvedLine(Product Product, ProductVariant? Variant, int Quantity, string? Problem, decimal GstFactor, DateTime Now)
    {
        /// <summary>The product's offer, only while it is actually running (stored offer prices can lag by a few seconds).</summary>
        public Offer? Offer => Product.Offer is { } o && Product.OfferPrice is not null && OfferService.IsLive(o, Now) ? o : null;
        public decimal RegularPrice => Variant?.SellingPrice ?? Product.SellingPrice;
        public decimal UnitPrice => Offer is null ? RegularPrice
            : Variant is null ? Product.OfferPrice!.Value : Variant.OfferPrice ?? Variant.SellingPrice;
        public decimal OfferSaving => (RegularPrice - UnitPrice) * Quantity;
        public decimal UnitMrp => Variant?.Mrp ?? Product.Mrp;
        public decimal UnitVendorPrice => Variant?.VendorPrice ?? Product.VendorPrice;
        /// <summary>What we pay the vendor per unit, including GST.</summary>
        public decimal UnitCost => Math.Round(UnitVendorPrice * GstFactor, 2, MidpointRounding.AwayFromZero);
        public decimal LineTotal => UnitPrice * Quantity;
    }

    private async Task<List<ResolvedLine>> ResolveAsync(List<CartLineRequest> items)
    {
        var ids = items.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Products.Include(p => p.Variants).ThenInclude(v => v.OptionValues).ThenInclude(v => v.Option)
            .Include(p => p.Images).Include(p => p.Category).Include(p => p.Offer)
            .AsSplitQuery().Where(p => ids.Contains(p.Id)).ToListAsync();
        var settings = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var now = DateTime.UtcNow;

        var lines = new List<ResolvedLine>();
        foreach (var item in items.Where(i => i.Quantity > 0).GroupBy(i => (i.ProductId, i.VariantId)))
        {
            var p = products.FirstOrDefault(x => x.Id == item.Key.ProductId);
            if (p is null) continue;
            var qty = Math.Min(item.Sum(i => i.Quantity), 20);
            ProductVariant? v = null;
            string? problem = null;
            if (item.Key.VariantId is not null)
            {
                v = p.Variants.FirstOrDefault(x => x.Id == item.Key.VariantId && x.IsActive);
                if (v is null) problem = "This option is no longer available.";
                else if (!v.IsAvailable) problem = "Out of stock";
            }
            else if (p.Variants.Any(x => x.IsActive)) problem = "Please choose an option.";
            if (!p.IsActive || !p.IsAvailable) problem = "Out of stock";
            lines.Add(new ResolvedLine(p, v, qty, problem, PricingService.GstFactor(p.GstRate, settings), now));
        }
        return lines;
    }

    public async Task<(Coupon? coupon, decimal discount, string? message)> ApplyCouponAsync(string? code, decimal subTotal, int? userId)
    {
        if (string.IsNullOrWhiteSpace(code)) return (null, 0, null);
        var now = DateTime.UtcNow;
        var c = await db.Coupons.FirstOrDefaultAsync(x => x.Code == code.Trim().ToUpper());
        if (c is null || !c.IsActive) return (null, 0, "Invalid coupon code.");
        if (c.StartsAt > now || c.EndsAt < now) return (null, 0, "This coupon has expired.");
        if (c.UsageLimit is not null && c.UsedCount >= c.UsageLimit) return (null, 0, "This coupon has reached its usage limit.");
        if (subTotal < c.MinOrderAmount) return (null, 0, $"Add items worth ₹{c.MinOrderAmount - subTotal:N0} more to use this coupon.");
        if (userId is not null && c.PerUserLimit is not null)
        {
            var used = await db.Orders.CountAsync(o => o.UserId == userId && o.CouponCode == c.Code && o.Status != OrderStatus.Cancelled);
            if (used >= c.PerUserLimit) return (null, 0, "You have already used this coupon.");
        }
        var discount = c.DiscountType == DiscountType.Percent ? subTotal * c.Value / 100m : c.Value;
        if (c.MaxDiscount is not null) discount = Math.Min(discount, c.MaxDiscount.Value);
        discount = Math.Floor(Math.Min(discount, subTotal));
        return (c, discount, $"Coupon applied — you save ₹{discount:N0}.");
    }

    public async Task<PincodeCheckDto> CheckPincodeAsync(string? pincode)
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        pincode = pincode?.Trim();
        if (string.IsNullOrEmpty(pincode) || pincode.Length != 6 || !pincode.All(char.IsDigit))
            return new PincodeCheckDto(pincode ?? "", false, 0, 0, false, null, "Enter a valid 6-digit pincode.");
        var pin = await db.ServiceablePincodes.AsNoTracking().FirstOrDefaultAsync(p => p.Pincode == pincode && p.IsActive);
        if (pin is null)
            return s.RestrictToServiceablePincodes
                ? new PincodeCheckDto(pincode, false, 0, 0, false, null, "Sorry, we don't deliver to this pincode yet. We currently serve Bengaluru.")
                : new PincodeCheckDto(pincode, true, s.DefaultDeliveryDays + 3, 0, false, null, "Delivery available. Cash on delivery not available.");
        return new PincodeCheckDto(pincode, true, pin.DeliveryDays, pin.ExtraCharge, pin.CodAvailable && s.CodEnabled, pin.Area,
            $"Delivery in {pin.DeliveryDays}–{pin.DeliveryDays + 3} days{(pin.Area is null ? "" : " to " + pin.Area)}.");
    }

    public async Task<QuoteDto> QuoteAsync(QuoteRequest req, int? userId)
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var lines = await ResolveAsync(req.Items);
        var messages = new List<string>();
        var valid = lines.Where(l => l.Problem is null).ToList();
        var subTotal = valid.Sum(l => l.LineTotal);
        var mrpTotal = valid.Sum(l => l.UnitMrp * l.Quantity);
        var (coupon, discount, couponMsg) = await ApplyCouponAsync(req.CouponCode, subTotal, userId);

        PincodeCheckDto? pin = null;
        if (!string.IsNullOrWhiteSpace(req.Pincode))
        {
            pin = await CheckPincodeAsync(req.Pincode);
            if (!pin.Serviceable) messages.Add(pin.Message);
        }

        var shipping = s.FreeShippingThreshold > 0 && subTotal - discount >= s.FreeShippingThreshold ? 0 : s.ShippingFee;
        if (subTotal == 0) shipping = 0;
        shipping += pin?.ExtraCharge ?? 0;

        var codAvailable = s.CodEnabled && (pin?.CodAvailable ?? true) && subTotal - discount + shipping <= s.CodMaxOrderAmount;
        var codFee = req.PaymentMethod == PaymentMethod.CashOnDelivery && codAvailable ? s.CodFee : 0;
        var total = subTotal - discount + shipping + codFee;
        var tax = ComputeTax(valid.Select(l => (l.LineTotal, l.Product.GstRate)).ToList(), discount, shipping + codFee, s.GstRegistered);

        if (lines.Any(l => l.Problem is not null)) messages.Add("Some items in your cart are unavailable — please remove them to continue.");
        if (req.PaymentMethod == PaymentMethod.CashOnDelivery && !codAvailable) messages.Add("Cash on delivery is not available for this order.");

        var deliveryDays = Math.Max(pin?.DeliveryDays ?? s.DefaultDeliveryDays, valid.Count == 0 ? 0 : valid.Max(l => l.Product.DeliveryDays));
        return new QuoteDto(
            lines.Select(l => new QuoteLineDto(l.Product.Id, l.Variant?.Id, l.Product.Name, l.Variant?.Name, l.Product.Slug,
                CatalogService.ThumbUrl(l.Product.Images.Where(i => i.MediaType == MediaType.Image).OrderBy(i => i.SortOrder).FirstOrDefault()),
                l.Quantity, l.UnitMrp, l.UnitPrice, l.LineTotal, l.Problem is null, l.Problem, l.RegularPrice, l.Offer?.Name)).ToList(),
            subTotal, mrpTotal, discount, coupon?.Code, couponMsg, shipping, codFee, total, tax,
            valid.Count > 0 && lines.All(l => l.Problem is null) && (pin?.Serviceable ?? true),
            codAvailable, deliveryDays, messages, valid.Sum(l => l.OfferSaving));
    }

    /// <summary>GST contained in GST-inclusive prices, after distributing the discount proportionally.</summary>
    private static decimal ComputeTax(List<(decimal lineTotal, decimal rate)> lines, decimal discount, decimal charges, bool gstRegistered)
    {
        if (!gstRegistered) return 0;
        var sub = lines.Sum(l => l.lineTotal);
        decimal tax = 0;
        foreach (var (lineTotal, rate) in lines)
        {
            var share = sub == 0 ? 0 : discount * lineTotal / sub;
            tax += Money.SplitInclusive(lineTotal - share, rate).tax;
        }
        tax += Money.SplitInclusive(charges, 18).tax;
        return Money.R2(tax);
    }

    // ---------- Place order ----------

    private async Task<string> NextOrderNumberAsync()
    {
        var ist = FinancialYear.ToIst(DateTime.UtcNow);
        var prefix = $"WC{ist:yyMMdd}";
        var last = await db.Orders.Where(o => o.OrderNumber.StartsWith(prefix)).OrderByDescending(o => o.OrderNumber).Select(o => o.OrderNumber).FirstOrDefaultAsync();
        var seq = last is null ? 1 : int.Parse(last[prefix.Length..]) + 1;
        return $"{prefix}{seq:0000}";
    }

    /// <summary>
    /// Guest checkout: orders are attached to a password-less "guest" customer keyed by email, so they show up
    /// in the customer's account if they register (or already have one) with the same email.
    /// </summary>
    private async Task<AppUser> GetOrCreateGuestAsync(string? rawEmail, AddressRequest address)
    {
        var email = rawEmail?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || !email.Contains('@') || email.Length > 200) throw new AppException("Please enter a valid email address.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            user = new AppUser { Email = email, FullName = address.FullName.Trim(), Phone = address.Phone.Trim(), IsGuest = true, Role = UserRole.Customer };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        else if (!user.IsActive || user.Role != UserRole.Customer)
            throw new AppException("Please sign in to place this order, or contact us for help.");
        else if (user.IsGuest)
        {
            user.FullName = address.FullName.Trim();
            user.Phone = address.Phone.Trim();
        }
        return user;
    }

    /// <summary>Places an order for a signed-in customer (userId) or a guest (userId null, GuestEmail + Address required).</summary>
    public async Task<PlaceOrderResponse> PlaceOrderAsync(int? userId, PlaceOrderRequest req)
    {
        var isGuest = userId is null;
        AppUser user;
        Address address;
        if (isGuest)
        {
            if (req.Address is null) throw new AppException("Delivery address is required.");
            address = MapAddress(new Address(), req.Address);
            user = await GetOrCreateGuestAsync(req.GuestEmail, req.Address with { Phone = address.Phone });
        }
        else
        {
            user = await db.Users.FindAsync(userId) ?? throw new AppException("User not found", 404);
            if (req.AddressId is not null)
                address = await db.Addresses.FirstOrDefaultAsync(a => a.Id == req.AddressId && a.UserId == userId) ?? throw new AppException("Address not found.");
            else if (req.Address is not null)
            {
                address = MapAddress(new Address { UserId = user.Id }, req.Address);
                if (!await db.Addresses.AnyAsync(a => a.UserId == userId)) address.IsDefault = true;
                db.Addresses.Add(address);
            }
            else throw new AppException("Shipping address is required.");
        }

        // Which provider processes the chosen method (UPI → direct UPI, cards → gateway, …) is configured in Admin.
        var provider = await payments.ResolveProviderAsync(req.PaymentMethod);

        var quote = await QuoteAsync(new QuoteRequest(req.Items, req.CouponCode, address.Pincode, req.PaymentMethod), user.Id);
        if (!quote.CanCheckout) throw new AppException(quote.Messages.FirstOrDefault() ?? "Your cart cannot be checked out.");
        if (req.PaymentMethod == PaymentMethod.CashOnDelivery && !quote.CodAvailable) throw new AppException("Cash on delivery is not available for this order.");
        if (!string.IsNullOrWhiteSpace(req.CouponCode) && quote.CouponCode is null) throw new AppException(quote.CouponMessage ?? "Invalid coupon.");

        var lines = await ResolveAsync(req.Items);
        var order = new Order
        {
            OrderNumber = await NextOrderNumberAsync(),
            UserId = user.Id,
            PaymentMethod = req.PaymentMethod,
            PaymentProvider = provider,
            ShipName = address.FullName, ShipPhone = address.Phone, ShipEmail = user.Email,
            ShipLine1 = address.Line1, ShipLine2 = address.Line2, ShipLandmark = address.Landmark,
            ShipCity = address.City, ShipState = address.State, ShipPincode = address.Pincode,
            CustomerGstin = string.IsNullOrWhiteSpace(req.CustomerGstin) ? null : req.CustomerGstin.Trim().ToUpper(),
            SubTotal = quote.SubTotal, Discount = quote.Discount, CouponCode = quote.CouponCode, OfferDiscount = lines.Sum(l => l.OfferSaving),
            ShippingFee = quote.ShippingFee, CodFee = quote.CodFee, Total = quote.Total, TaxAmount = quote.TaxAmount,
            CustomerNotes = req.Notes?.Trim(),
            ExpectedDeliveryDate = DateTime.UtcNow.Date.AddDays(quote.DeliveryDays + 2),
            EstimatedCost = lines.Sum(l => l.UnitCost * l.Quantity)
        };
        foreach (var l in lines)
            order.Items.Add(new OrderItem
            {
                ProductId = l.Product.Id, VariantId = l.Variant?.Id,
                ProductName = l.Product.Name, VariantName = l.Variant?.Name, VendorVariantName = l.Variant is null ? null : ProductOptions.VendorFacingName(l.Variant, l.Product),
                ImageUrl = CatalogService.ThumbUrl(l.Product.Images.Where(i => i.MediaType == MediaType.Image).OrderBy(i => i.SortOrder).FirstOrDefault()),
                HsnCode = l.Product.HsnCode, GstRate = l.Product.GstRate,
                Quantity = l.Quantity, UnitMrp = l.UnitMrp, UnitPrice = l.UnitPrice, LineTotal = l.LineTotal,
                UnitRegularPrice = l.RegularPrice, OfferId = l.Offer?.Id, OfferName = l.Offer?.Name,
                UnitVendorPrice = l.UnitVendorPrice, UnitVendorCost = l.UnitCost, VendorProductId = l.Product.VendorProductId, VendorUrl = l.Product.VendorUrl
            });
        order.History.Add(new OrderStatusHistory { Status = OrderStatus.Pending, Note = "Order placed", CreatedBy = user.FullName });

        if (quote.CouponCode is not null)
            await db.Coupons.Where(c => c.Code == quote.CouponCode).ExecuteUpdateAsync(c => c.SetProperty(x => x.UsedCount, x => x.UsedCount + 1));

        if (provider == PaymentProviders.DirectUpi) order.UpiAmount = await UniqueUpiAmountAsync(order.Total);

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // Items purchased are removed from the server-side cart.
        var bought = lines.Select(l => l.Product.Id).ToList();
        await db.CartItems.Where(c => c.UserId == user.Id && bought.Contains(c.ProductId)).ExecuteDeleteAsync();

        string? rzpOrderId = null;
        var paise = (long)Math.Round(order.Total * 100);
        if (provider == PaymentProviders.Razorpay)
        {
            rzpOrderId = await razorpay.CreateOrderAsync(paise, order.OrderNumber);
            order.RazorpayOrderId = rzpOrderId;
            await db.SaveChangesAsync();
        }
        else
        {
            await NotifyOrderPlacedAsync(order);
        }

        return new PlaceOrderResponse(order.OrderNumber, order.Total, order.PaymentMethod, rzpOrderId, (await razorpay.GetOptionsAsync()).KeyId,
            paise, order.ShipName, order.ShipEmail, order.ShipPhone, order.AccessToken, isGuest, provider, GatewayMethod(order.PaymentMethod));
    }

    /// <summary>Razorpay checkout "method" prefill for the customer's choice.</summary>
    private static string? GatewayMethod(PaymentMethod m) => m switch
    {
        PaymentMethod.Upi => "upi", PaymentMethod.Card => "card", PaymentMethod.NetBanking => "netbanking",
        PaymentMethod.Wallet => "wallet", PaymentMethod.Emi => "emi", _ => null
    };

    /// <summary>
    /// Direct UPI: the amount to pay is the total minus 1–99 "fingerprint" paise, unique among unpaid orders,
    /// so a bank credit alert of exactly that amount identifies the order.
    /// </summary>
    private async Task<decimal> UniqueUpiAmountAsync(decimal total)
    {
        var (_, _, values) = await payments.GetAsync(PaymentProviders.DirectUpi);
        if (values.GetValueOrDefault("uniquePaise") is "false" or "False") return total;
        var since = DateTime.UtcNow.AddDays(-14);
        var baseAmount = Math.Floor(total);
        var taken = (await db.Orders
                .Where(o => o.UpiAmount != null && o.PaymentStatus == PaymentStatus.Pending && o.Status != OrderStatus.Cancelled && o.CreatedAt > since
                            && o.UpiAmount < baseAmount + 1 && o.UpiAmount > baseAmount - 2)
                .Select(o => o.UpiAmount!.Value).ToListAsync())
            .ToHashSet();
        var candidates = Enumerable.Range(1, 99).Select(p => baseAmount - p / 100m)
            .Concat(Enumerable.Range(1, 99).Select(p => baseAmount - 1 - p / 100m))
            .Where(a => !taken.Contains(a)).ToList();
        if (candidates.Count == 0) return total;
        return candidates[Random.Shared.Next(Math.Min(candidates.Count, 99))];
    }

    public async Task<PaymentInstructionsDto?> InstructionsAsync(Order o)
    {
        if (o.PaymentStatus == PaymentStatus.Paid || o.Status == OrderStatus.Cancelled) return null;
        var awaiting = !string.IsNullOrEmpty(o.CustomerUtr);
        if (o.PaymentProvider == PaymentProviders.DirectUpi)
        {
            var (_, _, v) = await payments.GetAsync(PaymentProviders.DirectUpi);
            var vpa = v.GetValueOrDefault("upiId");
            var payee = v.GetValueOrDefault("payeeName") ?? "WoodsyCart";
            var amount = (o.UpiAmount ?? o.Total) - o.AmountPaid;
            var uri = vpa is null ? null :
                $"upi://pay?pa={Uri.EscapeDataString(vpa)}&pn={Uri.EscapeDataString(payee)}&am={amount:0.00}&cu=INR" +
                $"&tn={Uri.EscapeDataString("WoodsyCart order " + o.OrderNumber)}&tr={Uri.EscapeDataString(o.OrderNumber)}";
            return new PaymentInstructionsDto(PaymentProviders.DirectUpi, amount, vpa, payee, uri, null, null, null, null, o.CustomerUtr, awaiting);
        }
        if (o.PaymentProvider == PaymentProviders.BankTransfer)
        {
            var (_, _, v) = await payments.GetAsync(PaymentProviders.BankTransfer);
            return new PaymentInstructionsDto(PaymentProviders.BankTransfer, o.Total - o.AmountPaid, null, null, null,
                v.GetValueOrDefault("accountName"), v.GetValueOrDefault("accountNumber"), v.GetValueOrDefault("ifsc"), v.GetValueOrDefault("bankName"),
                o.CustomerUtr, awaiting);
        }
        return null;
    }

    /// <summary>
    /// Records a payment confirmed outside a gateway (UPI/bank alert, verified UTR, or entered by admin)
    /// and confirms the order once it is fully paid. Small paise differences from the UPI fingerprint count as paid.
    /// </summary>
    public async Task ConfirmPaymentAsync(Order order, decimal amount, string? reference, string note, string by)
    {
        if (!string.IsNullOrEmpty(reference) && await db.Payments.AnyAsync(p => p.OrderId == order.Id && p.Reference == reference)) return;
        db.Payments.Add(new Payment
        {
            OrderId = order.Id, Method = order.PaymentMethod, Amount = amount, IsSuccessful = true, Reference = reference, Notes = note
        });
        order.AmountPaid += amount;
        var fullyPaid = order.AmountPaid >= order.Total - 1m;
        order.PaymentStatus = fullyPaid ? PaymentStatus.Paid : PaymentStatus.PartiallyPaid;
        db.OrderStatusHistory.Add(new OrderStatusHistory
        {
            OrderId = order.Id, Status = order.Status, VisibleToCustomer = false, CreatedBy = by,
            Note = $"₹{amount:N2} received{(reference is null ? "" : $" (ref {reference})")} — {note}"
        });
        if (fullyPaid && order.Status == OrderStatus.Pending)
        {
            order.Status = OrderStatus.Confirmed;
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = order.Id, Status = OrderStatus.Confirmed, Note = "Payment received — thank you!" });
        }
        await db.SaveChangesAsync();
        if (fullyPaid)
            await email.SendAsync(order.ShipEmail, $"Payment received for order {order.OrderNumber}",
                $"<p>We've received your payment of ₹{order.AmountPaid:N2} for order <b>{order.OrderNumber}</b>. Your furniture is now being prepared.</p><p>— Team WoodsyCart</p>");
    }

    /// <summary>Customer reports the UPI/bank reference after paying; it is matched against received alerts or verified by admin.</summary>
    public async Task<OrderDetailDto> SubmitUtrAsync(OrderAccess access, string orderNumber, string utr)
    {
        var order = await FindAsync(orderNumber, access);
        utr = new string((utr ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (utr.Length is < 10 or > 22) throw new AppException("Enter the 12-digit UPI reference (UTR) shown in your payment app.");
        if (order.PaymentStatus == PaymentStatus.Paid) return (await GetOrderAsync(orderNumber, access))!;
        if (order.Status == OrderStatus.Cancelled) throw new AppException("This order was cancelled.");
        if (await db.Orders.AnyAsync(o => o.Id != order.Id && o.CustomerUtr == utr)) throw new AppException("This reference was already used for another order.");
        order.CustomerUtr = utr;
        db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = order.Id, Status = order.Status, Note = $"Customer submitted payment reference {utr}", VisibleToCustomer = false, CreatedBy = "Customer" });
        await db.SaveChangesAsync();

        // If the bank alert already arrived, match it now.
        var alert = await db.PaymentAlerts.FirstOrDefaultAsync(a => a.Utr == utr && a.Status == PaymentAlertStatus.Unmatched);
        if (alert is { Amount: not null })
        {
            alert.Status = PaymentAlertStatus.Matched;
            alert.OrderId = order.Id;
            await ConfirmPaymentAsync(order, alert.Amount.Value, utr, "matched by customer's UTR", "System");
        }
        return (await GetOrderAsync(orderNumber, access))!;
    }

    /// <summary>Loads an order the caller may act on: their own, or one whose access token they hold.</summary>
    private async Task<Order> FindAsync(string orderNumber, OrderAccess access)
    {
        if (access.UserId is null && string.IsNullOrEmpty(access.Token)) throw new AppException("Order not found", 404);
        int? userId = access.UserId;
        var token = string.IsNullOrEmpty(access.Token) ? null : access.Token;
        return await db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == orderNumber &&
                   ((userId != null && o.UserId == userId) || (token != null && o.AccessToken == token)))
               ?? throw new AppException("Order not found", 404);
    }

    public async Task<OrderDetailDto> VerifyPaymentAsync(OrderAccess access, VerifyPaymentRequest req)
    {
        var order = await FindAsync(req.OrderNumber, access);
        if (order.RazorpayOrderId != req.RazorpayOrderId || !await razorpay.VerifySignatureAsync(req.RazorpayOrderId, req.RazorpayPaymentId, req.RazorpaySignature))
            throw new AppException("Payment verification failed. If money was debited it will be refunded automatically.");
        await MarkOnlinePaymentAsync(order, req.RazorpayPaymentId);
        return (await GetOrderAsync(order.OrderNumber, access))!;
    }

    /// <summary>Creates a fresh Razorpay order for an unpaid online order (customer closed the payment window earlier).</summary>
    public async Task<PlaceOrderResponse> RetryPaymentAsync(OrderAccess access, string orderNumber)
    {
        var order = await FindAsync(orderNumber, access);
        var viaGateway = order.PaymentProvider == PaymentProviders.Razorpay || (order.PaymentProvider is null && order.PaymentMethod == PaymentMethod.Razorpay);
        if (!viaGateway || order.PaymentStatus == PaymentStatus.Paid || order.Status != OrderStatus.Pending)
            throw new AppException("This order doesn't need an online payment.");
        var paise = (long)Math.Round(order.Total * 100);
        order.RazorpayOrderId = await razorpay.CreateOrderAsync(paise, order.OrderNumber);
        await db.SaveChangesAsync();
        return new PlaceOrderResponse(order.OrderNumber, order.Total, order.PaymentMethod, order.RazorpayOrderId, (await razorpay.GetOptionsAsync()).KeyId,
            paise, order.ShipName, order.ShipEmail, order.ShipPhone, order.AccessToken, access.UserId is null, PaymentProviders.Razorpay, GatewayMethod(order.PaymentMethod));
    }

    public async Task MarkOnlinePaymentAsync(Order order, string paymentId)
    {
        if (await db.Payments.AnyAsync(p => p.GatewayPaymentId == paymentId)) return;
        db.Payments.Add(new Payment
        {
            OrderId = order.Id, Method = order.PaymentMethod, Amount = order.Total, IsSuccessful = true,
            GatewayOrderId = order.RazorpayOrderId, GatewayPaymentId = paymentId
        });
        order.AmountPaid += order.Total;
        order.PaymentStatus = PaymentStatus.Paid;
        if (order.Status == OrderStatus.Pending)
        {
            order.Status = OrderStatus.Confirmed;
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = order.Id, Status = OrderStatus.Confirmed, Note = "Payment received online" });
        }
        await db.SaveChangesAsync();
        await NotifyOrderPlacedAsync(order);
    }

    private async Task NotifyOrderPlacedAsync(Order order)
    {
        // The link carries the order's access token so guests can open their order without an account.
        var storefront = email.StorefrontUrl?.TrimEnd('/');
        var link = string.IsNullOrEmpty(storefront) ? "" :
            $"<p><a href=\"{storefront}/order/{order.OrderNumber}?token={order.AccessToken}\">View or track your order</a></p>";
        var html = $"<h2>Thank you for your order!</h2><p>Your order <b>{order.OrderNumber}</b> of ₹{order.Total:N2} has been received. " +
                   $"We will notify you as soon as it is dispatched.</p>{link}<p>— Team WoodsyCart</p>";
        await email.SendAsync(order.ShipEmail, $"Order {order.OrderNumber} received", html);
        await email.SendAsync(email.AdminEmail, $"New order {order.OrderNumber} — ₹{order.Total:N0}",
            $"<p>New order from {order.ShipName} ({order.ShipPhone}), {order.ShipCity} {order.ShipPincode}.</p>");
    }

    /// <summary>
    /// Moves the expected delivery date (e.g. the vendor is delayed). The first promised date is kept, the change and its reason
    /// appear on the customer's order timeline, and the customer is emailed when <paramref name="notify"/> is set.
    /// </summary>
    public async Task ChangeDeliveryDateAsync(Order o, DateTime newDate, string? reason, string by, bool notify)
    {
        if (o.Status is OrderStatus.Delivered or OrderStatus.Cancelled or OrderStatus.Returned)
            throw new AppException("This order is already closed — its delivery date can't be changed.");
        var date = DateTime.SpecifyKind(newDate.Date, DateTimeKind.Utc);
        var todayIst = FinancialYear.ToIst(DateTime.UtcNow).Date;
        if (date < todayIst) throw new AppException("The new delivery date can't be in the past.");
        var old = o.ExpectedDeliveryDate?.Date;
        if (old == date) throw new AppException("That's already the expected delivery date.");
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var later = old is null || date > old;
        if (later && old is not null && reason is null) throw new AppException("Tell the customer why the delivery is later than promised.");

        o.OriginalDeliveryDate ??= old;
        o.ExpectedDeliveryDate = date;
        o.DeliveryDateReason = reason;
        o.DeliveryDateChangedAt = DateTime.UtcNow;

        var when = date.ToString("ddd, d MMM yyyy");
        var note = old is null ? $"Expected delivery: {when}."
            : later ? $"Delivery date moved from {old:ddd, d MMM} to {when}."
            : $"Good news — delivery brought forward from {old:ddd, d MMM} to {when}.";
        if (reason is not null) note += $" Reason: {reason}";
        db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = o.Id, Status = o.Status, Note = note, VisibleToCustomer = true, CreatedBy = by });
        await db.SaveChangesAsync();

        if (!notify) return;
        var storefront = email.StorefrontUrl?.TrimEnd('/');
        var link = string.IsNullOrEmpty(storefront) ? "" :
            $"<p><a href=\"{storefront}/order/{o.OrderNumber}?token={o.AccessToken}\">View your order</a></p>";
        var html = later && old is not null
            ? $"<p>Hi {System.Net.WebUtility.HtmlEncode(o.ShipName)},</p><p>We're sorry — the delivery of your order <b>{o.OrderNumber}</b> will be a little later than planned.</p>" +
              $"<p>New expected delivery date: <b>{when}</b> (was {old:ddd, d MMM}).</p>" +
              (reason is null ? "" : $"<p>Reason: {System.Net.WebUtility.HtmlEncode(reason)}</p>") +
              $"<p>Thank you for your patience. Reply to this email or WhatsApp us if you have any questions.</p>{link}<p>— Team WoodsyCart</p>"
            : $"<p>Hi {System.Net.WebUtility.HtmlEncode(o.ShipName)},</p><p>Your order <b>{o.OrderNumber}</b> is now expected on <b>{when}</b>.</p>" +
              (reason is null ? "" : $"<p>{System.Net.WebUtility.HtmlEncode(reason)}</p>") + $"{link}<p>— Team WoodsyCart</p>";
        await email.SendAsync(o.ShipEmail, later && old is not null ? $"Order {o.OrderNumber}: new delivery date {date:d MMM}" : $"Order {o.OrderNumber}: delivery on {date:d MMM}", html);
    }

    public static Address MapAddress(Address a, AddressRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.FullName) || string.IsNullOrWhiteSpace(r.Line1) || string.IsNullOrWhiteSpace(r.Phone))
            throw new AppException("Name, phone and address line are required.");
        if (r.Pincode?.Trim().Length != 6) throw new AppException("Enter a valid 6-digit pincode.");
        a.Label = string.IsNullOrWhiteSpace(r.Label) ? "Home" : r.Label.Trim();
        a.FullName = r.FullName.Trim(); a.Phone = Mobile.Normalise(r.Phone);
        a.Line1 = r.Line1.Trim(); a.Line2 = r.Line2?.Trim(); a.Landmark = r.Landmark?.Trim();
        a.City = r.City.Trim(); a.State = r.State.Trim(); a.Pincode = r.Pincode.Trim();
        a.IsDefault = r.IsDefault;
        return a;
    }

    // ---------- Customer order views ----------

    public async Task<List<OrderSummaryDto>> GetMyOrdersAsync(int userId) =>
        await db.Orders.AsNoTracking().Where(o => o.UserId == userId).OrderByDescending(o => o.CreatedAt)
            .Select(o => new OrderSummaryDto(o.Id, o.OrderNumber, o.CreatedAt, o.Status, o.PaymentStatus, o.PaymentMethod, o.Total,
                o.Items.Sum(i => i.Quantity), o.Items.Select(i => i.ImageUrl).FirstOrDefault(), o.Items.Select(i => i.ProductName).FirstOrDefault(),
                o.ExpectedDeliveryDate, o.DeliveryDateChangedAt))
            .ToListAsync();

    public async Task<OrderDetailDto?> GetOrderAsync(string orderNumber, OrderAccess? access, string? phone = null)
    {
        var q = db.Orders.AsNoTracking().Include(o => o.Items).ThenInclude(i => i.Product).Include(o => o.History).Include(o => o.Invoices)
            .AsSplitQuery().Where(o => o.OrderNumber == orderNumber);
        int? userId = access?.UserId;
        var token = string.IsNullOrEmpty(access?.Token) ? null : access.Token;
        if (userId is not null || token is not null)
            q = q.Where(o => (userId != null && o.UserId == userId) || (token != null && o.AccessToken == token));
        else if (phone is not null) q = q.Where(o => o.ShipPhone.EndsWith(phone));
        else return null;
        var o = await q.FirstOrDefaultAsync();
        return o is null ? null : ToDetail(o) with { Payment = await InstructionsAsync(o) };
    }

    public static OrderDetailDto ToDetail(Order o)
    {
        var invoice = o.Invoices.Where(i => !i.IsCancelled).OrderByDescending(i => i.Id).FirstOrDefault();
        return new OrderDetailDto(o.Id, o.OrderNumber, o.CreatedAt, o.Status, o.PaymentStatus, o.PaymentMethod,
            o.ShipName, o.ShipPhone, o.ShipEmail, o.ShipLine1, o.ShipLine2, o.ShipLandmark, o.ShipCity, o.ShipState, o.ShipPincode,
            o.SubTotal, o.Discount, o.CouponCode, o.ShippingFee, o.CodFee, o.Total, o.TaxAmount, o.AmountPaid,
            o.CustomerNotes, o.ExpectedDeliveryDate, o.DeliveredAt, o.TrackingNumber, o.CourierName, o.CancelReason,
            o.Items.Select(i => new OrderItemDto(i.Id, i.ProductId, i.VariantId, i.ProductName, i.VariantName, i.ImageUrl, i.Product?.Slug,
                i.Quantity, i.UnitMrp, i.UnitPrice, i.LineTotal, i.GstRate, i.HsnCode, i.UnitRegularPrice, i.OfferName)).ToList(),
            o.History.Where(h => h.VisibleToCustomer).OrderBy(h => h.CreatedAt).Select(h => new OrderHistoryDto(h.Status, h.Note, h.CreatedAt)).ToList(),
            CanCancel(o), CanReturn(o), invoice is not null, invoice?.InvoiceNumber, o.PaymentProvider, null,
            o.OriginalDeliveryDate, o.DeliveryDateReason, o.DeliveryDateChangedAt);
    }

    public static bool CanCancel(Order o) => o.Status is OrderStatus.Pending or OrderStatus.Confirmed;

    public static bool CanReturn(Order o) => o.Status == OrderStatus.Delivered && o.DeliveredAt is not null &&
                                             o.DeliveredAt.Value.AddDays(ReturnWindowDays) >= DateTime.UtcNow;

    public async Task CancelByCustomerAsync(OrderAccess access, string orderNumber, string reason)
    {
        var o = await FindAsync(orderNumber, access);
        if (!CanCancel(o)) throw new AppException("This order can no longer be cancelled. Please contact support.");
        await CancelAsync(o, reason, "Customer");
    }

    public async Task CancelAsync(Order o, string reason, string by)
    {
        o.Status = OrderStatus.Cancelled;
        o.CancelledAt = DateTime.UtcNow;
        o.CancelReason = reason;
        db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = o.Id, Status = OrderStatus.Cancelled, Note = $"Cancelled: {reason}", CreatedBy = by });
        if (o.CouponCode is not null)
            await db.Coupons.Where(c => c.Code == o.CouponCode && c.UsedCount > 0).ExecuteUpdateAsync(c => c.SetProperty(x => x.UsedCount, x => x.UsedCount - 1));
        if (o.AmountPaid > 0 && !await db.Refunds.AnyAsync(r => r.OrderId == o.Id && r.Type == RefundType.Cancellation))
            db.Refunds.Add(new Refund { OrderId = o.Id, Type = RefundType.Cancellation, Amount = o.AmountPaid, Reason = reason, Status = RefundStatus.Approved });
        await db.SaveChangesAsync();
        await email.SendAsync(o.ShipEmail, $"Order {o.OrderNumber} cancelled",
            $"<p>Your order {o.OrderNumber} has been cancelled.{(o.AmountPaid > 0 ? " Your refund of ₹" + o.AmountPaid.ToString("N2") + " will be processed within 5–7 working days." : "")}</p>");
    }

    public async Task RequestReturnAsync(OrderAccess access, string orderNumber, ReturnRequest req)
    {
        var o = await FindAsync(orderNumber, access);
        if (!CanReturn(o)) throw new AppException($"Returns are accepted within {ReturnWindowDays} days of delivery.");
        if (await db.Refunds.AnyAsync(r => r.OrderId == o.Id && r.Type == RefundType.Return)) throw new AppException("A return request already exists for this order.");
        db.Refunds.Add(new Refund { OrderId = o.Id, Type = RefundType.Return, Amount = o.AmountPaid > 0 ? o.AmountPaid : o.Total, Reason = req.Reason, CustomerComment = req.Comment });
        db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = o.Id, Status = o.Status, Note = $"Return requested: {req.Reason}", CreatedBy = "Customer" });
        await db.SaveChangesAsync();
        await email.SendAsync(email.AdminEmail, $"Return requested for {o.OrderNumber}", $"<p>Reason: {req.Reason}</p><p>{req.Comment}</p>");
    }
}
