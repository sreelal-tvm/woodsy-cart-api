using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Services;
using WoodsyCart.Infrastructure.Vendors;

namespace WoodsyCart.Api.Controllers.Admin;

public class VendorsController(AppDbContext db) : AdminControllerBase
{
    [HttpGet]
    public async Task<object> List() => await db.Vendors.AsNoTracking().OrderBy(v => v.Name).Select(v => new
    {
        v.Id, v.Name, v.ContactPerson, v.Phone, v.WhatsApp, v.Email, v.Address, v.City, v.State, v.Gstin, v.Website,
        v.BankDetails, v.PaymentTerms, v.ExternalStoreId, v.SyncEnabled, v.IsActive,
        Products = db.Products.Count(p => p.VendorId == v.Id),
        OutstandingAmount = db.VendorBills.Where(b => b.VendorId == v.Id && b.PaymentStatus != BillPaymentStatus.Paid).Sum(b => (decimal?)(b.TotalAmount - b.AmountPaid)) ?? 0,
        TotalPurchases = db.VendorBills.Where(b => b.VendorId == v.Id).Sum(b => (decimal?)b.TotalAmount) ?? 0
    }).ToListAsync();

    [HttpPost]
    public async Task<IActionResult> Create(Vendor v)
    {
        v.Id = 0;
        db.Vendors.Add(v);
        await db.SaveChangesAsync();
        return Ok(new { v.Id });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, Vendor r)
    {
        var v = await db.Vendors.FindAsync(id) ?? throw new AppException("Vendor not found", 404);
        v.Name = r.Name; v.ContactPerson = r.ContactPerson; v.Phone = r.Phone; v.WhatsApp = r.WhatsApp; v.Email = r.Email;
        v.Address = r.Address; v.City = r.City; v.State = r.State; v.Gstin = r.Gstin; v.Website = r.Website; v.BankDetails = r.BankDetails;
        v.PaymentTerms = r.PaymentTerms; v.ExternalStoreId = r.ExternalStoreId; v.SyncEnabled = r.SyncEnabled; v.IsActive = r.IsActive;
        await db.SaveChangesAsync();
        return NoContent();
    }
}

public record StartSyncRequest(int VendorId, bool DownloadImages = true, bool PublishNewProducts = true);

[Route("api/admin/vendor-sync")]
public class VendorSyncController(AppDbContext db, VendorSyncQueue queue, SyncProgress progress, IServiceScopeFactory scopes, ILogger<VendorSyncController> logger) : AdminControllerBase
{
    /// <summary>Shrinks oversized product photos already on disk and fixes YouTube links saved as images. Runs in the background.</summary>
    [HttpPost("optimize-images")]
    public IActionResult OptimizeImages()
    {
        if (progress.IsRunning) throw new AppException("A sync is already running.");
        progress.IsRunning = true;
        progress.LastError = null;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var (optimised, videos, saved) = await scope.ServiceProvider.GetRequiredService<VendorSyncService>().OptimizeExistingAsync(CancellationToken.None);
                logger.LogInformation("Optimised {Count} images ({Mb} MB saved), fixed {Videos} videos", optimised, saved / 1_048_576, videos);
            }
            catch (Exception ex) { progress.LastError = ex.Message; logger.LogError(ex, "Image optimisation failed"); }
            finally { progress.IsRunning = false; progress.Stage = "Idle"; }
        });
        return Accepted(progress);
    }

    [HttpPost("start")]
    public IActionResult Start(StartSyncRequest r)
    {
        if (progress.IsRunning) throw new AppException("A sync is already running.");
        progress.IsRunning = true;
        progress.Stage = "Queued";
        progress.Processed = 0;
        progress.Total = 0;
        if (!queue.Enqueue(new SyncRequest(r.VendorId, new SyncOptions { DownloadImages = r.DownloadImages, PublishNewProducts = r.PublishNewProducts })))
        {
            progress.IsRunning = false;
            throw new AppException("Sync queue is full, try again shortly.");
        }
        return Accepted(progress);
    }

    [HttpGet("status")]
    public SyncProgress Status() => progress;

    [HttpGet("logs")]
    public Task<List<VendorSyncLog>> Logs() => db.VendorSyncLogs.AsNoTracking().OrderByDescending(l => l.Id).Take(30).ToListAsync();
}

public record VendorBillRow(int Id, int VendorId, string VendorName, int? OrderId, string? OrderNumber, string BillNumber, DateTime BillDate, DateTime? DueDate,
    decimal TaxableAmount, decimal GstAmount, decimal ShippingCharges, decimal TotalAmount, decimal AmountPaid, BillPaymentStatus PaymentStatus,
    DateTime? LastPaidOn, string? PaymentReference, string? FileUrl, string? Notes);
public record VendorBillUpsert(int VendorId, int? OrderId, string BillNumber, DateTime BillDate, DateTime? DueDate, decimal TaxableAmount, decimal GstAmount,
    decimal ShippingCharges, string? FileUrl, string? Notes);
public record BillPaymentRequest(decimal Amount, DateTime? PaidOn, string? Reference);

[Route("api/admin/vendor-bills")]
public class VendorBillsController(AppDbContext db) : AdminControllerBase
{
    [HttpGet]
    public async Task<PagedResult<VendorBillRow>> List(int? vendorId, BillPaymentStatus? status, DateTime? from, DateTime? to, string? search, int page = 1, int pageSize = 25)
    {
        var q = db.VendorBills.AsNoTracking();
        if (vendorId is not null) q = q.Where(b => b.VendorId == vendorId);
        if (status is not null) q = q.Where(b => b.PaymentStatus == status);
        if (from is not null) q = q.Where(b => b.BillDate >= from);
        if (to is not null) q = q.Where(b => b.BillDate < to.Value.AddDays(1));
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(b => b.BillNumber.Contains(search) || (b.Order != null && b.Order.OrderNumber.Contains(search)));
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(b => b.BillDate).ThenByDescending(b => b.Id).Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize)
            .Select(b => new VendorBillRow(b.Id, b.VendorId, b.Vendor!.Name, b.OrderId, b.Order != null ? b.Order.OrderNumber : null, b.BillNumber, b.BillDate, b.DueDate,
                b.TaxableAmount, b.GstAmount, b.ShippingCharges, b.TotalAmount, b.AmountPaid, b.PaymentStatus, b.LastPaidOn, b.PaymentReference, b.FileUrl, b.Notes))
            .ToListAsync();
        return new PagedResult<VendorBillRow> { Items = items, Total = total, Page = page, PageSize = pageSize };
    }

    [HttpGet("summary")]
    public async Task<object> Summary()
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return new
        {
            outstanding = await db.VendorBills.Where(b => b.PaymentStatus != BillPaymentStatus.Paid).SumAsync(b => (decimal?)(b.TotalAmount - b.AmountPaid)) ?? 0,
            overdue = await db.VendorBills.Where(b => b.PaymentStatus != BillPaymentStatus.Paid && b.DueDate < now).SumAsync(b => (decimal?)(b.TotalAmount - b.AmountPaid)) ?? 0,
            thisMonth = await db.VendorBills.Where(b => b.BillDate >= monthStart).SumAsync(b => (decimal?)b.TotalAmount) ?? 0,
            ordersWithoutBill = await db.Orders.CountAsync(o => o.Status >= OrderStatus.PlacedWithVendor && o.Status <= OrderStatus.Delivered && !db.VendorBills.Any(b => b.OrderId == o.Id))
        };
    }

    [HttpPost]
    public async Task<IActionResult> Create(VendorBillUpsert r)
    {
        var b = new VendorBill();
        await Map(b, r);
        db.VendorBills.Add(b);
        await db.SaveChangesAsync();
        return Ok(new { b.Id });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, VendorBillUpsert r)
    {
        var b = await db.VendorBills.FindAsync(id) ?? throw new AppException("Bill not found", 404);
        await Map(b, r);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task Map(VendorBill b, VendorBillUpsert r)
    {
        if (string.IsNullOrWhiteSpace(r.BillNumber)) throw new AppException("Bill number is required.");
        b.VendorId = r.VendorId; b.OrderId = r.OrderId; b.BillNumber = r.BillNumber.Trim(); b.BillDate = r.BillDate; b.DueDate = r.DueDate;
        b.TaxableAmount = r.TaxableAmount; b.GstAmount = r.GstAmount; b.ShippingCharges = r.ShippingCharges;
        b.TotalAmount = r.TaxableAmount + r.GstAmount + r.ShippingCharges;
        b.FileUrl = r.FileUrl; b.Notes = r.Notes;
        b.PurchaseOrderId = r.OrderId is null ? null : await db.PurchaseOrders.Where(p => p.OrderId == r.OrderId).Select(p => (int?)p.Id).FirstOrDefaultAsync();
        b.PaymentStatus = b.AmountPaid <= 0 ? BillPaymentStatus.Unpaid : b.AmountPaid >= b.TotalAmount ? BillPaymentStatus.Paid : BillPaymentStatus.PartiallyPaid;
    }

    [HttpPost("{id:int}/payments")]
    public async Task<IActionResult> Pay(int id, BillPaymentRequest r)
    {
        var b = await db.VendorBills.FindAsync(id) ?? throw new AppException("Bill not found", 404);
        if (r.Amount <= 0) throw new AppException("Amount must be positive.");
        b.AmountPaid += r.Amount;
        b.LastPaidOn = r.PaidOn ?? DateTime.UtcNow;
        b.PaymentReference = r.Reference ?? b.PaymentReference;
        b.PaymentStatus = b.AmountPaid >= b.TotalAmount ? BillPaymentStatus.Paid : BillPaymentStatus.PartiallyPaid;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await db.VendorBills.Where(b => b.Id == id).ExecuteDeleteAsync();
        return NoContent();
    }
}

public record ExpenseUpsert(DateTime Date, ExpenseCategory Category, string Description, decimal Amount, decimal GstAmount, string? PaidTo,
    string? PaymentMode, string? Reference, int? OrderId, string? ReceiptUrl);

public class ExpensesController(AppDbContext db) : AdminControllerBase
{
    [HttpGet]
    public async Task<object> List(DateTime? from, DateTime? to, ExpenseCategory? category)
    {
        var q = db.Expenses.AsNoTracking();
        if (from is not null) q = q.Where(e => e.Date >= from);
        if (to is not null) q = q.Where(e => e.Date < to.Value.AddDays(1));
        if (category is not null) q = q.Where(e => e.Category == category);
        var items = await q.OrderByDescending(e => e.Date).ThenByDescending(e => e.Id).ToListAsync();
        return new
        {
            items,
            total = items.Sum(e => e.Amount + e.GstAmount),
            byCategory = items.GroupBy(e => e.Category).Select(g => new { category = g.Key, amount = g.Sum(e => e.Amount + e.GstAmount) }).OrderByDescending(x => x.amount)
        };
    }

    [HttpPost]
    public async Task<IActionResult> Create(ExpenseUpsert r)
    {
        db.Expenses.Add(Map(new Expense(), r));
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, ExpenseUpsert r)
    {
        var e = await db.Expenses.FindAsync(id) ?? throw new AppException("Expense not found", 404);
        Map(e, r);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await db.Expenses.Where(e => e.Id == id).ExecuteDeleteAsync();
        return NoContent();
    }

    private static Expense Map(Expense e, ExpenseUpsert r)
    {
        if (string.IsNullOrWhiteSpace(r.Description) || r.Amount <= 0) throw new AppException("Description and a positive amount are required.");
        e.Date = r.Date; e.Category = r.Category; e.Description = r.Description.Trim(); e.Amount = r.Amount; e.GstAmount = r.GstAmount;
        e.PaidTo = r.PaidTo; e.PaymentMode = r.PaymentMode; e.Reference = r.Reference; e.OrderId = r.OrderId; e.ReceiptUrl = r.ReceiptUrl;
        return e;
    }
}

public class ReportsController(ReportService reports) : AdminControllerBase
{
    private static (DateTime from, DateTime to) Range(DateTime? from, DateTime? to, int defaultDays)
    {
        var t = (to ?? DateTime.UtcNow.Date).Date.AddDays(1);
        var f = (from ?? t.AddDays(-defaultDays)).Date;
        return (DateTime.SpecifyKind(f, DateTimeKind.Utc), DateTime.SpecifyKind(t, DateTimeKind.Utc));
    }

    [HttpGet("dashboard")]
    public Task<DashboardDto> Dashboard(DateTime? from, DateTime? to)
    {
        var (f, t) = Range(from, to, 30);
        return reports.GetDashboardAsync(f, t);
    }

    [HttpGet("profit-loss")]
    public Task<ProfitLossDto> ProfitLoss(DateTime? from, DateTime? to)
    {
        var (f, t) = Range(from, to, 90);
        return reports.GetProfitLossAsync(f, t);
    }

    [HttpGet("profit-loss/export")]
    public async Task<IActionResult> ExportProfitLoss(DateTime? from, DateTime? to)
    {
        var (f, t) = Range(from, to, 90);
        var p = await reports.GetProfitLossAsync(f, t);
        var sb = new StringBuilder();
        sb.AppendLine($"WoodsyCart Profit & Loss,{f:dd-MM-yyyy} to {t.AddDays(-1):dd-MM-yyyy}");
        sb.AppendLine($"Gross sales,{p.GrossSales}");
        sb.AppendLine($"Less: GST collected,{p.OutputGst}");
        sb.AppendLine($"Less: Refunds,{p.Refunds}");
        sb.AppendLine($"Net revenue,{p.NetRevenue}");
        sb.AppendLine($"Cost of goods (vendor),{p.CostOfGoods}");
        sb.AppendLine($"Gross profit,{p.GrossProfit}");
        foreach (var e in p.Expenses) sb.AppendLine($"Expense: {e.Label},{e.Amount}");
        sb.AppendLine($"Total expenses,{p.TotalExpenses}");
        sb.AppendLine($"Net profit,{p.NetProfit}");
        sb.AppendLine();
        sb.AppendLine("Order,Channel,Delivered,Customer,Revenue (ex GST),Cost,Cost estimated,Profit,Margin %");
        foreach (var o in p.Orders) sb.AppendLine($"{o.OrderNumber},{o.Channel},{o.Date:dd-MM-yyyy},\"{o.Customer}\",{o.Revenue},{o.Cost},{o.CostIsEstimated},{o.Profit},{o.MarginPercent}");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "profit-loss.csv");
    }
}

public class CouponsController(AppDbContext db) : AdminControllerBase
{
    [HttpGet]
    public Task<List<Coupon>> List() => db.Coupons.AsNoTracking().OrderByDescending(c => c.Id).ToListAsync();

    [HttpPost]
    public async Task<IActionResult> Create(Coupon c)
    {
        c.Id = 0; c.Code = c.Code.Trim().ToUpper(); c.UsedCount = 0;
        if (await db.Coupons.AnyAsync(x => x.Code == c.Code)) throw new AppException("Coupon code already exists.");
        db.Coupons.Add(c);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, Coupon r)
    {
        var c = await db.Coupons.FindAsync(id) ?? throw new AppException("Coupon not found", 404);
        c.Code = r.Code.Trim().ToUpper(); c.Description = r.Description; c.DiscountType = r.DiscountType; c.Value = r.Value; c.MaxDiscount = r.MaxDiscount;
        c.MinOrderAmount = r.MinOrderAmount; c.StartsAt = r.StartsAt; c.EndsAt = r.EndsAt; c.UsageLimit = r.UsageLimit; c.PerUserLimit = r.PerUserLimit; c.IsActive = r.IsActive;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await db.Coupons.Where(c => c.Id == id).ExecuteDeleteAsync();
        return NoContent();
    }
}

public class ReviewsController(AppDbContext db) : AdminControllerBase
{
    [HttpGet]
    public async Task<object> List(bool? approved) => await db.Reviews.AsNoTracking()
        .Where(r => approved == null || r.IsApproved == approved).OrderByDescending(r => r.CreatedAt).Take(200)
        .Select(r => new { r.Id, r.ProductId, Product = r.Product!.Name, ProductSlug = r.Product.Slug, Customer = r.User!.FullName, r.OrderId, OrderNumber = r.Order != null ? r.Order.OrderNumber : null, r.Rating, r.Title, r.Comment, r.IsApproved, r.IsVerifiedPurchase, r.CreatedAt })
        .ToListAsync();

    [HttpPost("{id:int}/approve")]
    public async Task<IActionResult> Approve(int id, bool approved = true)
    {
        var r = await db.Reviews.FindAsync(id) ?? throw new AppException("Review not found", 404);
        r.IsApproved = approved;
        await db.SaveChangesAsync();
        await CatalogService.RefreshRatingAsync(db, r.ProductId);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await db.Reviews.FindAsync(id) ?? throw new AppException("Review not found", 404);
        db.Reviews.Remove(r);
        await db.SaveChangesAsync();
        await CatalogService.RefreshRatingAsync(db, r.ProductId);
        return NoContent();
    }
}

public class BannersController(AppDbContext db) : AdminControllerBase
{
    [HttpGet]
    public Task<List<Banner>> List() => db.Banners.AsNoTracking().OrderBy(b => b.SortOrder).ToListAsync();

    [HttpPost]
    public async Task<IActionResult> Create(Banner b)
    {
        b.Id = 0;
        db.Banners.Add(b);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, Banner r)
    {
        var b = await db.Banners.FindAsync(id) ?? throw new AppException("Banner not found", 404);
        b.Title = r.Title; b.Subtitle = r.Subtitle; b.ImageUrl = r.ImageUrl; b.LinkUrl = r.LinkUrl; b.ButtonText = r.ButtonText; b.SortOrder = r.SortOrder; b.IsActive = r.IsActive;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await db.Banners.Where(b => b.Id == id).ExecuteDeleteAsync();
        return NoContent();
    }
}

public record BulkPincodes(string Pincodes, int DeliveryDays, decimal ExtraCharge, bool CodAvailable, int? DeliveryDaysMax = null);

public class PincodesController(AppDbContext db) : AdminControllerBase
{
    [HttpGet]
    public Task<List<ServiceablePincode>> List() => db.ServiceablePincodes.AsNoTracking().OrderBy(p => p.Pincode).ToListAsync();

    [HttpPost("bulk")]
    public async Task<object> Bulk(BulkPincodes r)
    {
        var pins = r.Pincodes.Split([',', '\n', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length == 6 && p.All(char.IsDigit)).Distinct().ToList();
        var existing = await db.ServiceablePincodes.Where(p => pins.Contains(p.Pincode)).ToListAsync();
        foreach (var pin in pins)
        {
            var e = existing.FirstOrDefault(x => x.Pincode == pin);
            if (e is null) { e = new ServiceablePincode { Pincode = pin }; db.ServiceablePincodes.Add(e); }
            e.DeliveryDays = r.DeliveryDays; e.DeliveryDaysMax = MaxDays(r.DeliveryDays, r.DeliveryDaysMax); e.ExtraCharge = r.ExtraCharge; e.CodAvailable = r.CodAvailable; e.IsActive = true;
        }
        await db.SaveChangesAsync();
        return new { saved = pins.Count };
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, ServiceablePincode r)
    {
        var p = await db.ServiceablePincodes.FindAsync(id) ?? throw new AppException("Pincode not found", 404);
        p.Area = r.Area; p.City = r.City; p.DeliveryDays = r.DeliveryDays; p.DeliveryDaysMax = MaxDays(r.DeliveryDays, r.DeliveryDaysMax);
        p.ExtraCharge = r.ExtraCharge; p.CodAvailable = r.CodAvailable; p.IsActive = r.IsActive;
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>"Up to" days must not be before "from" days; empty means from + 3.</summary>
    private static int? MaxDays(int from, int? max) => max is > 0 ? Math.Max(max.Value, from) : null;

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await db.ServiceablePincodes.Where(p => p.Id == id).ExecuteDeleteAsync();
        return NoContent();
    }
}

public record EnquiryUpdate(EnquiryStatus Status, string? AdminNotes);

public class EnquiriesController(AppDbContext db) : AdminControllerBase
{
    [HttpGet]
    public async Task<object> List(EnquiryStatus? status) => await db.Enquiries.AsNoTracking()
        .Where(e => status == null || e.Status == status).OrderByDescending(e => e.CreatedAt).Take(300)
        .Select(e => new { e.Id, e.Name, e.Email, e.Phone, e.Subject, e.Message, e.Status, e.AdminNotes, e.CreatedAt, e.ProductId, Product = e.Product != null ? e.Product.Name : null })
        .ToListAsync();

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, EnquiryUpdate r)
    {
        await db.Enquiries.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, r.Status).SetProperty(e => e.AdminNotes, r.AdminNotes));
        return NoContent();
    }
}

public class SettingsController(AppDbContext db, PricingService pricing) : AdminControllerBase
{
    [HttpGet]
    public Task<BusinessSettings> Get() => db.BusinessSettings.AsNoTracking().FirstAsync();

    [HttpPut]
    public async Task<object> Update(BusinessSettings r)
    {
        var s = await db.BusinessSettings.FirstAsync();
        if (r.DefaultGstRate is < 0 or > 28) throw new AppException("GST % must be between 0 and 28.");
        r.GoogleMapsApiKey = string.IsNullOrWhiteSpace(r.GoogleMapsApiKey) ? null : r.GoogleMapsApiKey.Trim();
        if (r.MapsEnabled && r.GoogleMapsApiKey is null) throw new AppException("Paste your Google Maps API key before switching maps on.");
        var repriced = s.DefaultCommissionPercent != r.DefaultCommissionPercent || s.PriceRounding != r.PriceRounding
                       || s.VendorPricesIncludeGst != r.VendorPricesIncludeGst;
        var id = s.Id; var created = s.CreatedAt;
        db.Entry(s).CurrentValues.SetValues(r);
        s.Id = id; s.CreatedAt = created;
        s.InvoicePrefix = string.IsNullOrWhiteSpace(r.InvoicePrefix) ? "WC" : r.InvoicePrefix.Trim().ToUpper();
        await db.SaveChangesAsync();
        var updated = repriced ? await pricing.RecalculateAsync() : 0;
        return new { repricedProducts = updated };
    }
}

public class UploadsController(FileStorage storage) : AdminControllerBase
{
    private static readonly HashSet<string> Folders = ["banners", "categories", "bills", "receipts", "branding", "misc"];

    [HttpPost]
    [RequestSizeLimit(15_000_000)]
    public async Task<object> Upload(IFormFile file, string folder = "misc")
    {
        if (!Folders.Contains(folder)) folder = "misc";
        await using var s = file.OpenReadStream();
        return new { url = await storage.SaveAsync(s, folder, file.FileName) };
    }
}

public record StaffUpsert(string FullName, string Email, string? Phone, UserRole Role, string? Password, bool IsActive);

[Authorize(Roles = "Admin")]
public class StaffController(AppDbContext db, AuthService auth) : AdminControllerBase
{
    [HttpGet]
    public async Task<object> List() => await db.Users.AsNoTracking().Where(u => u.Role != UserRole.Customer)
        .Select(u => new { u.Id, u.FullName, u.Email, u.Phone, u.Role, u.IsActive, u.LastLoginAt }).ToListAsync();

    [HttpPost]
    public async Task<IActionResult> Create(StaffUpsert r)
    {
        var email = r.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email)) throw new AppException("Email already in use.");
        if (string.IsNullOrWhiteSpace(r.Password) || r.Password.Length < 8) throw new AppException("Password must be at least 8 characters.");
        var u = new AppUser { FullName = r.FullName, Email = email, Phone = r.Phone, Role = r.Role == UserRole.Customer ? UserRole.Staff : r.Role, IsActive = r.IsActive };
        u.PasswordHash = auth.HashPassword(u, r.Password);
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, StaffUpsert r)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id && x.Role != UserRole.Customer) ?? throw new AppException("User not found", 404);
        if (id == UserId && !r.IsActive) throw new AppException("You cannot deactivate yourself.");
        u.FullName = r.FullName; u.Phone = r.Phone; u.IsActive = r.IsActive;
        if (r.Role != UserRole.Customer) u.Role = r.Role;
        if (!string.IsNullOrWhiteSpace(r.Password))
        {
            if (r.Password.Length < 8) throw new AppException("Password must be at least 8 characters.");
            u.PasswordHash = auth.HashPassword(u, r.Password);
        }
        await db.SaveChangesAsync();
        return NoContent();
    }
}
