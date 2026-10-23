using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Infrastructure.Payments.Gateways;

/// <summary>
/// Safety net for PhonePe / Cashfree / PayU / Paytm: every few minutes, unpaid gateway orders from the last 3 days
/// are checked with the gateway, so a payment is recorded even if the customer closed the browser before returning.
/// </summary>
public class GatewayReconcileWorker(IServiceScopeFactory scopes, ILogger<GatewayReconcileWorker> log) : BackgroundService
{
    private static readonly string[] Codes = [PaymentProviders.PhonePe, PaymentProviders.Cashfree, PaymentProviders.PayU, PaymentProviders.PaytmPg];

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stop);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var orders = scope.ServiceProvider.GetRequiredService<OrderService>();
                var since = DateTime.UtcNow.AddDays(-3);
                var pending = await db.Orders
                    .Where(o => o.PaymentStatus == PaymentStatus.Pending && o.Status == OrderStatus.Pending && o.GatewayReference != null
                                && Codes.Contains(o.PaymentProvider!) && o.CreatedAt > since)
                    .OrderByDescending(o => o.CreatedAt).Take(50).ToListAsync(stop);
                foreach (var o in pending)
                    if (await orders.ReconcileGatewayAsync(o, ct: stop) == GatewayState.Paid)
                        log.LogInformation("Gateway payment found for order {Order}", o.OrderNumber);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Gateway payment check failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(3), stop);
        }
    }

    /// <summary>
    /// Finds our payment-attempt id in a gateway callback or webhook body (form or JSON), whatever the gateway calls it:
    /// PayU "txnid", Paytm "ORDERID", Cashfree "order_id", PhonePe "merchantOrderId".
    /// </summary>
    public static string? FindMerchantOrderId(string body, IDictionary<string, string>? form)
    {
        string[] keys = ["txnid", "ORDERID", "orderId", "merchantOrderId", "originalMerchantOrderId", "order_id"];
        if (form is not null)
            foreach (var k in keys)
                if (form.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] is not ('{' or '[')) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Search(doc.RootElement, keys, 0);
        }
        catch (JsonException) { return null; }
    }

    private static string? Search(JsonElement e, string[] keys, int depth)
    {
        if (depth > 6) return null;
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var k in keys)
                if (e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) return s;
            foreach (var p in e.EnumerateObject())
                if (Search(p.Value, keys, depth + 1) is { } found) return found;
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray())
                if (Search(item, keys, depth + 1) is { } found) return found;
        return null;
    }
}
