using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using WoodsyCart.Api.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Contracts;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Payments;
using WoodsyCart.Domain;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Api.Controllers;

[Route("api/auth")]
public class AuthController(AuthService auth, AppDbContext db) : ApiControllerBase
{
    [EnableRateLimiting(Traffic.Auth)]
    [HttpPost("register")]
    public Task<AuthResponse> Register(RegisterRequest req) => auth.RegisterAsync(req);

    [EnableRateLimiting(Traffic.Auth)]
    [HttpPost("login")]
    public Task<AuthResponse> Login(LoginRequest req) => auth.LoginAsync(req, adminOnly: false);

    /// <summary>Always answers the same way, so it can't be used to find out who has an account.</summary>
    [EnableRateLimiting(Traffic.Auth)]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest req)
    {
        await auth.ForgotPasswordAsync(req);
        return Ok(new { message = $"If an account exists for this email, we've sent a link to reset the password. It expires in {AuthService.ResetLinkMinutes} minutes." });
    }

    [EnableRateLimiting(Traffic.Auth)]
    [HttpPost("reset-password")]
    public Task<AuthResponse> ResetPassword(ResetPasswordRequest req) => auth.ResetPasswordAsync(req);

    [EnableRateLimiting(Traffic.Auth)]
    [HttpPost("admin/login")]
    public Task<AuthResponse> AdminLogin(LoginRequest req) => auth.LoginAsync(req, adminOnly: true);

    /// <summary>Staff only; the link opens the admin app. Answers the same way whether or not the account exists.</summary>
    [EnableRateLimiting(Traffic.Auth)]
    [HttpPost("admin/forgot-password")]
    public async Task<IActionResult> AdminForgotPassword(ForgotPasswordRequest req)
    {
        await auth.ForgotPasswordAsync(req, admin: true);
        return Ok(new { message = $"If a staff account exists for this email, we've sent a link to reset the password. It expires in {AuthService.ResetLinkMinutes} minutes." });
    }

    [EnableRateLimiting(Traffic.Auth)]
    [HttpPost("admin/reset-password")]
    public Task<AuthResponse> AdminResetPassword(ResetPasswordRequest req) => auth.ResetPasswordAsync(req, admin: true);

    [Authorize, HttpGet("me")]
    public async Task<UserDto> Me() => AuthService.ToDto(await db.Users.FindAsync(UserId) ?? throw new AppException("Not found", 404));

    [Authorize, HttpPut("me")]
    public async Task<UserDto> UpdateMe(UpdateProfileRequest req)
    {
        var u = await db.Users.FindAsync(UserId) ?? throw new AppException("Not found", 404);
        if (string.IsNullOrWhiteSpace(req.FullName)) throw new AppException("Name is required.");
        u.FullName = req.FullName.Trim();
        u.Phone = Mobile.NormaliseOptional(req.Phone);
        await db.SaveChangesAsync();
        return AuthService.ToDto(u);
    }

    [Authorize, HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req)
    {
        await auth.ChangePasswordAsync(UserId, req);
        return NoContent();
    }
}

[Route("api/catalog")]
public class CatalogController(CatalogService catalog) : ApiControllerBase
{
    [OutputCache(PolicyName = Traffic.Catalog)]
    [HttpGet("home")]
    public Task<HomeDto> Home() => catalog.GetHomeAsync();

    [OutputCache(PolicyName = Traffic.Catalog)]
    [HttpGet("categories")]
    public Task<List<CategoryDto>> Categories() => catalog.GetCategoriesAsync();

    [OutputCache(PolicyName = Traffic.Catalog)]
    [HttpGet("products")]
    public Task<PagedResult<ProductCardDto>> Products([FromQuery] ProductQuery q) => catalog.SearchAsync(q);

    [OutputCache(PolicyName = Traffic.Catalog)]
    [HttpGet("facets")]
    public Task<FacetsDto> Facets([FromQuery] ProductQuery q) => catalog.GetFacetsAsync(q);

    [OutputCache(PolicyName = Traffic.Catalog)]
    [HttpGet("products/{slugOrId}")]
    public async Task<ProductDetailDto> Product(string slugOrId) =>
        await catalog.GetProductAsync(slugOrId) ?? throw new AppException("Product not found", 404);

    [OutputCache(PolicyName = Traffic.Catalog)]
    [HttpGet("offers")]
    public Task<List<OfferDto>> Offers() => catalog.GetLiveOffersAsync();

    [OutputCache(PolicyName = Traffic.Catalog)]
    [HttpGet("offers/{slug}")]
    public async Task<OfferDto> Offer(string slug) => await catalog.GetOfferAsync(slug) ?? throw new AppException("Offer not found", 404);

    [OutputCache(PolicyName = Traffic.Catalog)]
    [HttpGet("products/{id:int}/related")]
    public Task<List<ProductCardDto>> Related(int id) => catalog.GetRelatedAsync(id);

    [OutputCache(PolicyName = Traffic.Catalog)]
    [HttpGet("products/{id:int}/reviews")]
    public Task<PagedResult<ReviewDto>> Reviews(int id, int page = 1) => catalog.GetReviewsAsync(id, Math.Max(1, page));

    [Authorize, HttpGet("products/{id:int}/my-review")]
    public Task<MyReviewStatusDto> MyReview(int id) => catalog.GetMyReviewStatusAsync(id, UserId);

    [EnableRateLimiting(Traffic.Forms)]
    [Authorize, HttpPost("products/{id:int}/reviews")]
    public async Task<IActionResult> AddReview(int id, CreateReviewRequest req)
    {
        await catalog.AddReviewAsync(id, UserId, req);
        return Ok(new { message = "Thanks! Your review will appear once approved." });
    }
}

[Route("api/site")]
public class SiteController(AppDbContext db, OrderService orders, PaymentConfigService payments, RazorpayService razorpay, EmailSender email) : ApiControllerBase
{
    /// <summary>Payment methods offered at checkout (configured in Admin → Payment integrations).</summary>
    [OutputCache(PolicyName = Traffic.Catalog)]
    [HttpGet("payment-methods")]
    public Task<List<CheckoutMethodDto>> PaymentMethods() => payments.GetCheckoutMethodsAsync();

    [OutputCache(PolicyName = Traffic.Catalog)]
    [HttpGet("settings")]
    [HttpHead("settings")] // uptime monitors check with HEAD
    public async Task<PublicSettingsDto> Settings()
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var methods = await payments.GetCheckoutMethodsAsync();
        var rzp = await razorpay.GetOptionsAsync();
        var online = methods.Any(m => PaymentProviders.Gateways.Contains(m.Provider));
        var address = string.Join(", ", new[] { s.AddressLine1, s.AddressLine2, s.City, s.State, s.Pincode }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        return new PublicSettingsDto(s.BusinessName, s.Tagline, s.Phone, s.WhatsApp, s.Email, address,
            s.ShippingFee, s.FreeShippingThreshold, s.CodEnabled, s.CodFee, s.CodMaxOrderAmount,
            online, methods.Any(m => m.Provider == PaymentProviders.Razorpay) ? rzp.KeyId : null,
            s.InstagramUrl, s.FacebookUrl, s.YoutubeUrl, s.DefaultDeliveryDays,
            s.MapsEnabled && !string.IsNullOrWhiteSpace(s.GoogleMapsApiKey) ? s.GoogleMapsApiKey : null);
    }

    [OutputCache(PolicyName = Traffic.Catalog)]
    [HttpGet("pincode/{pincode}")]
    public Task<PincodeCheckDto> Pincode(string pincode, double? lat, double? lng) => orders.CheckPincodeAsync(pincode, lat, lng);

    [EnableRateLimiting(Traffic.Forms)]
    [HttpPost("enquiry")]
    public async Task<IActionResult> Enquiry(EnquiryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Phone) || string.IsNullOrWhiteSpace(req.Message))
            throw new AppException("Name, phone and message are required.");
        var enquiry = new Enquiry
        {
            Name = req.Name.Trim(), Email = req.Email?.Trim(), Phone = Mobile.Normalise(req.Phone),
            Subject = req.Subject?.Trim(), Message = req.Message.Trim(), ProductId = req.ProductId
        };
        db.Enquiries.Add(enquiry);
        await db.SaveChangesAsync();
        var product = req.ProductId is null ? null : await db.Products.AsNoTracking().Where(p => p.Id == req.ProductId).Select(p => p.Name).FirstOrDefaultAsync();
        var E = (Func<string?, string>)WoodsyCart.Infrastructure.Email.EmailTemplates.E;
        await email.AdminsAsync(WoodsyCart.Infrastructure.Email.EmailEvent.AdminNewEnquiry, $"New enquiry from {enquiry.Name}{(product is null ? "" : " — " + product)}",
            "New enquiry",
            $"<p><b>{E(enquiry.Name)}</b> · {E(enquiry.Phone)}{(enquiry.Email is null ? "" : " · " + E(enquiry.Email))}</p>" +
            (product is null ? "" : $"<p>About: <b>{E(product)}</b></p>") + (enquiry.Subject is null ? "" : $"<p>Subject: {E(enquiry.Subject)}</p>") +
            WoodsyCart.Infrastructure.Email.EmailTemplates.Info(E(enquiry.Message).Replace("\n", "<br/>")),
            ("Reply on WhatsApp", $"https://wa.me/91{enquiry.Phone}"));
        await email.CustomerAsync(WoodsyCart.Infrastructure.Email.EmailEvent.EnquiryReceived, enquiry.Email, "We've received your message", "Thanks for getting in touch!",
            $"<p>Hi {E(enquiry.Name.Split(' ')[0])}, we've received your message{(product is null ? "" : $" about <b>{E(product)}</b>")} and will get back to you shortly — usually the same day.</p>" +
            WoodsyCart.Infrastructure.Email.EmailTemplates.Info(E(enquiry.Message).Replace("\n", "<br/>")),
            ("Browse furniture", email.StorefrontUrl + "/shop"));
        return Ok(new { message = "Thanks! We'll get back to you shortly." });
    }
}

[Authorize, Route("api/account")]
public class AccountController(AppDbContext db) : ApiControllerBase
{
    // ----- Addresses -----
    [HttpGet("addresses")]
    public Task<List<AddressDto>> Addresses() => db.Addresses.AsNoTracking().Where(a => a.UserId == UserId)
        .OrderByDescending(a => a.IsDefault).ThenByDescending(a => a.Id)
        .Select(a => new AddressDto(a.Id, a.Label, a.FullName, a.Phone, a.Line1, a.Line2, a.Landmark, a.City, a.State, a.Pincode, a.IsDefault, a.Latitude, a.Longitude))
        .ToListAsync();

    [HttpPost("addresses")]
    public async Task<AddressDto> AddAddress(AddressRequest req)
    {
        var a = OrderService.MapAddress(new Address { UserId = UserId }, req);
        if (!await db.Addresses.AnyAsync(x => x.UserId == UserId)) a.IsDefault = true;
        if (a.IsDefault) await ClearDefault();
        db.Addresses.Add(a);
        await db.SaveChangesAsync();
        return new AddressDto(a.Id, a.Label, a.FullName, a.Phone, a.Line1, a.Line2, a.Landmark, a.City, a.State, a.Pincode, a.IsDefault, a.Latitude, a.Longitude);
    }

    [HttpPut("addresses/{id:int}")]
    public async Task<IActionResult> UpdateAddress(int id, AddressRequest req)
    {
        var a = await db.Addresses.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId) ?? throw new AppException("Not found", 404);
        if (req.IsDefault) await ClearDefault();
        OrderService.MapAddress(a, req);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("addresses/{id:int}")]
    public async Task<IActionResult> DeleteAddress(int id)
    {
        await db.Addresses.Where(x => x.Id == id && x.UserId == UserId).ExecuteDeleteAsync();
        return NoContent();
    }

    private Task ClearDefault() => db.Addresses.Where(x => x.UserId == UserId && x.IsDefault).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDefault, false));

    // ----- Wishlist -----
    [HttpGet("wishlist")]
    public Task<List<ProductCardDto>> Wishlist()
    {
        var ids = db.WishlistItems.Where(w => w.UserId == UserId).Select(w => w.ProductId);
        return CatalogService.ToCards(db.Products.AsNoTracking().Where(p => ids.Contains(p.Id))).ToListAsync();
    }

    [HttpGet("wishlist/ids")]
    public Task<List<int>> WishlistIds() => db.WishlistItems.Where(w => w.UserId == UserId).Select(w => w.ProductId).ToListAsync();

    [HttpPost("wishlist/{productId:int}")]
    public async Task<IActionResult> AddWishlist(int productId)
    {
        if (!await db.WishlistItems.AnyAsync(w => w.UserId == UserId && w.ProductId == productId))
        {
            db.WishlistItems.Add(new WishlistItem { UserId = UserId, ProductId = productId });
            await db.SaveChangesAsync();
        }
        return NoContent();
    }

    [HttpDelete("wishlist/{productId:int}")]
    public async Task<IActionResult> RemoveWishlist(int productId)
    {
        await db.WishlistItems.Where(w => w.UserId == UserId && w.ProductId == productId).ExecuteDeleteAsync();
        return NoContent();
    }

    // ----- Cart (server copy for signed-in users, synced across devices) -----
    [HttpGet("cart")]
    public Task<List<CartLineRequest>> Cart() => db.CartItems.Where(c => c.UserId == UserId)
        .Select(c => new CartLineRequest(c.ProductId, c.VariantId, c.Quantity)).ToListAsync();

    /// <summary>Replaces the server cart with the client cart.</summary>
    [HttpPut("cart")]
    public async Task<IActionResult> SaveCart(List<CartLineRequest> lines)
    {
        await db.CartItems.Where(c => c.UserId == UserId).ExecuteDeleteAsync();
        var valid = await db.Products.Where(p => lines.Select(l => l.ProductId).Contains(p.Id)).Select(p => p.Id).ToListAsync();
        foreach (var l in lines.Where(l => valid.Contains(l.ProductId) && l.Quantity > 0).Take(50))
            db.CartItems.Add(new CartItem { UserId = UserId, ProductId = l.ProductId, VariantId = l.VariantId, Quantity = Math.Min(l.Quantity, 20) });
        await db.SaveChangesAsync();
        return NoContent();
    }
}

[Route("api/checkout")]
public class CheckoutController(OrderService orders) : ApiControllerBase
{
    [HttpPost("quote")]
    public Task<QuoteDto> Quote(QuoteRequest req) => orders.QuoteAsync(req, OptionalUserId);

    /// <summary>Signed-in customers use their saved addresses; guests send GuestEmail + Address.</summary>
    [EnableRateLimiting(Traffic.Orders)]
    [HttpPost("place-order")]
    public Task<PlaceOrderResponse> Place(PlaceOrderRequest req) => orders.PlaceOrderAsync(OptionalUserId, req);

    [HttpPost("verify-payment")]
    public Task<OrderDetailDto> Verify(VerifyPaymentRequest req) => orders.VerifyPaymentAsync(new OrderAccess(OptionalUserId, req.AccessToken), req);
}

[Route("api/orders")]
public class OrdersController(OrderService orders, InvoiceService invoices, AppDbContext db) : ApiControllerBase
{
    [Authorize, HttpGet]
    public Task<List<OrderSummaryDto>> Mine() => orders.GetMyOrdersAsync(UserId);

    /// <summary>Signed-in owner, or anyone with the order's access token (guest checkout / email link).</summary>
    private OrderAccess Access(string? token) => new(OptionalUserId, string.IsNullOrWhiteSpace(token) ? null : token.Trim());

    [HttpGet("{orderNumber}")]
    public async Task<OrderDetailDto> Get(string orderNumber, string? token) =>
        await orders.GetOrderAsync(orderNumber, Access(token)) ?? throw new AppException("Order not found", 404);

    /// <summary>Public tracking with order number + last digits of phone.</summary>
    [EnableRateLimiting(Traffic.Lookup)]
    [HttpGet("track")]
    public async Task<OrderDetailDto> Track(string orderNumber, string phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length < 4) throw new AppException("Enter the mobile number used for the order.");
        return await orders.GetOrderAsync(orderNumber.Trim().ToUpper(), null, digits[^4..])
               ?? throw new AppException("No order found with these details. Check the order number and the mobile number you gave at checkout.", 404);
    }

    [EnableRateLimiting(Traffic.Forms)]
    [HttpPost("{orderNumber}/cancel")]
    public async Task<IActionResult> Cancel(string orderNumber, CancelOrderRequest req, string? token)
    {
        await orders.CancelByCustomerAsync(Access(token), orderNumber, req.Reason);
        return NoContent();
    }

    /// <summary>Customer enters the UPI / bank reference after paying directly (no gateway).</summary>
    [EnableRateLimiting(Traffic.Forms)]
    [HttpPost("{orderNumber}/utr")]
    public Task<OrderDetailDto> SubmitUtr(string orderNumber, SubmitUtrRequest req, string? token) =>
        orders.SubmitUtrAsync(Access(token), orderNumber, req.Utr);

    [HttpPost("{orderNumber}/pay")]
    public Task<PlaceOrderResponse> Pay(string orderNumber, string? token) => orders.RetryPaymentAsync(Access(token), orderNumber);

    /// <summary>Order page asks the gateway (PhonePe / Cashfree / PayU / Paytm) whether a pending online payment went through.</summary>
    [HttpPost("{orderNumber}/check-payment")]
    public Task<OrderDetailDto> CheckPayment(string orderNumber, string? token) => orders.CheckPaymentAsync(Access(token), orderNumber);

    [EnableRateLimiting(Traffic.Forms)]
    [HttpPost("{orderNumber}/return")]
    public async Task<IActionResult> Return(string orderNumber, ReturnRequest req, string? token)
    {
        await orders.RequestReturnAsync(Access(token), orderNumber, req);
        return NoContent();
    }

    /// <summary>Rate / review a delivered item: signed-in owner, the order's access token, or the delivery phone (tracking page).</summary>
    [EnableRateLimiting(Traffic.Forms)]
    [HttpPost("{orderNumber}/reviews")]
    public Task<List<OrderReviewDto>> Review(string orderNumber, OrderReviewRequest req, string? token, string? phone) =>
        orders.ReviewItemAsync(Access(token), phone, orderNumber.Trim().ToUpper(), req);

    [HttpGet("{orderNumber}/invoice")]
    public async Task<IActionResult> Invoice(string orderNumber, string? token)
    {
        var access = Access(token);
        if (access.UserId is null && access.Token is null) throw new AppException("Invoice is not available yet.", 404);
        var inv = await db.CustomerInvoices.Where(i => i.Order!.OrderNumber == orderNumber && !i.IsCancelled &&
                ((access.UserId != null && i.Order.UserId == access.UserId) || (access.Token != null && i.Order.AccessToken == access.Token)))
            .OrderByDescending(i => i.Id).FirstOrDefaultAsync() ?? throw new AppException("Invoice is not available yet.", 404);
        var pdf = await invoices.RenderPdfAsync(inv.Id);
        return File(pdf, "application/pdf", $"{inv.InvoiceNumber.Replace('/', '-')}.pdf");
    }
}

[Route("api/payments")]
public class PaymentsController(RazorpayService razorpay, OrderService orders, AppDbContext db,
    PaymentConfigService payments, PaymentAlertService alerts, EmailSender email) : ApiControllerBase
{
    /// <summary>
    /// The customer comes back from PhonePe / Cashfree / PayU / Paytm (GET or form POST). The payment is checked with the
    /// gateway server-to-server, then the customer is sent to their order page. Nothing in the request is trusted.
    /// </summary>
    [AcceptVerbs("GET", "POST")]
    [Route("{code}/return/{orderNumber}")]
    public async Task<IActionResult> GatewayReturn(string code, string orderNumber)
    {
        var store = email.StorefrontUrl ?? "";
        var order = await db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);
        if (order is null || !string.Equals(order.PaymentProvider, code, StringComparison.OrdinalIgnoreCase)) return Redirect($"{store}/");
        var attempt = WoodsyCart.Infrastructure.Payments.Gateways.GatewayReconcileWorker.FindMerchantOrderId(await ReadBodyAsync(), await ReadFieldsAsync());
        var state = await orders.ReconcileGatewayAsync(order, attempt is not null && attempt.StartsWith(order.OrderNumber + "-") ? attempt : null);
        var result = state switch
        {
            WoodsyCart.Infrastructure.Payments.Gateways.GatewayState.Paid => "paid",
            WoodsyCart.Infrastructure.Payments.Gateways.GatewayState.Failed => "failed",
            _ => "pending"
        };
        // No access token here: anyone can call this URL. The order page uses the customer's sign-in or saved guest link.
        return Redirect($"{store}/order/{Uri.EscapeDataString(order.OrderNumber)}?payment={result}");
    }

    /// <summary>
    /// Payment notifications from PhonePe / Cashfree / PayU / Paytm. Only used as a trigger: the payment is confirmed
    /// with the gateway's own status API, so a forged notification can't mark an order paid.
    /// </summary>
    [HttpPost("{code}/webhook")]
    [RequestSizeLimit(100_000)]
    public async Task<IActionResult> GatewayWebhook(string code)
    {
        var id = WoodsyCart.Infrastructure.Payments.Gateways.GatewayReconcileWorker.FindMerchantOrderId(await ReadBodyAsync(), await ReadFieldsAsync());
        var dash = id?.LastIndexOf('-') ?? -1;
        if (id is null || dash <= 0) return Ok();
        var orderNumber = id[..dash];
        var order = await db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);
        if (order is not null && string.Equals(order.PaymentProvider, code, StringComparison.OrdinalIgnoreCase))
            await orders.ReconcileGatewayAsync(order, id);
        return Ok();
    }

    private string? _body;
    private async Task<string> ReadBodyAsync()
    {
        if (_body is not null) return _body;
        if (Request.HasFormContentType) return _body = "";
        using var reader = new StreamReader(Request.Body);
        return _body = await reader.ReadToEndAsync();
    }

    private async Task<Dictionary<string, string>> ReadFieldsAsync()
    {
        var fields = Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
        if (Request.HasFormContentType)
            foreach (var f in await Request.ReadFormAsync()) fields[f.Key] = f.Value.ToString();
        return fields;
    }

    /// <summary>
    /// Direct-UPI confirmation: the SMS/notification forwarder on the owner's phone posts every "money received"
    /// message here (plain text or JSON). Protected by the alert key from Admin → Payment integrations.
    /// </summary>
    [HttpPost("upi/alert")]
    [RequestSizeLimit(20_000)]
    public async Task<IActionResult> UpiAlert(string? key)
    {
        key ??= Request.Headers["X-Alert-Key"].ToString();
        var (_, _, values) = await payments.GetAsync(PaymentProviders.DirectUpi);
        var expected = values.GetValueOrDefault("alertKey");
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(key) ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(key)))
            return Unauthorized();

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
        var (text, sender) = PaymentAlertService.ExtractText(body);
        if (string.IsNullOrWhiteSpace(text)) return BadRequest(new { message = "Empty message." });
        var result = await alerts.ReceiveAsync(text, Request.Query["source"].FirstOrDefault() ?? "sms", sender);
        return Ok(new { result.Status, result.OrderNumber, result.Message });
    }

    /// <summary>Razorpay webhook (payment.captured) — safety net when the browser closes before verification.</summary>
    [HttpPost("razorpay/webhook")]
    public async Task<IActionResult> Webhook()
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
        if (!await razorpay.VerifyWebhookAsync(body, Request.Headers["X-Razorpay-Signature"].ToString())) return Unauthorized();

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        if (doc.RootElement.GetProperty("event").GetString() != "payment.captured") return Ok();
        var entity = doc.RootElement.GetProperty("payload").GetProperty("payment").GetProperty("entity");
        var rzpOrderId = entity.GetProperty("order_id").GetString();
        var paymentId = entity.GetProperty("id").GetString()!;
        var order = await db.Orders.FirstOrDefaultAsync(o => o.RazorpayOrderId == rzpOrderId);
        if (order is not null) await orders.MarkOnlinePaymentAsync(order, paymentId);
        return Ok();
    }
}
