# WoodsyCart API

ASP.NET Core 9 Web API for **WoodsyCart**, an online furniture store for Bengaluru that runs on a drop-shipping model:
products are imported from the vendor (GB Furniture), your commission is added on top, and orders are passed to the vendor, who delivers directly to the customer.

Used by:
- **woodsy-cart-web**: customer storefront (Angular PWA)
- **woodsy-cart-admin**: admin console (Angular PWA)

## Features

| Area | What it does |
| --- | --- |
| Catalogue | Categories, collections, tags, products, variants (size/colour), images and videos, reviews, home banners |
| Vendor sync | Imports the vendor's full DotPe/DotShowroom catalogue (products, variants, prices, images, YouTube videos), downloads and optimises images, detects vendor price changes, marks removed products unavailable, optional daily auto-sync |
| Pricing | Commission % (global → category → product override), price rounding (e.g. ₹12,499), manual price override, honest MRP |
| Checkout | Server-side quote, coupons, serviceable pincodes, COD limits, Razorpay online payments (with signature verification and webhook), retry payment |
| Orders | Status workflow (Pending → Confirmed → Placed with vendor → Dispatched → Out for delivery → Delivered), customer cancellation and returns, timeline, emails |
| Drop-ship | Purchase order per order (PDF), ready-to-send WhatsApp message for the vendor, vendor bills with file upload and payments |
| Finance | Customer invoices (GST tax invoice or bill of supply, FY numbering `WC/26-27/0001`, PDF), expenses, profit & loss, GST summary, cash position, CSV exports |
| Admin | Dashboard KPIs, customers, refunds, enquiries, pincodes, settings, staff users |

## Tech

.NET 9 · EF Core 9 (SQL Server) · JWT auth · QuestPDF (invoices, Community licence) · SkiaSharp (image optimisation) · Swagger

```
src/
  WoodsyCart.Domain/          entities and enums
  WoodsyCart.Infrastructure/  EF Core DbContext + migrations, services (catalog, orders, invoices, reports, pricing), vendor sync
  WoodsyCart.Api/             controllers (storefront + /api/admin/*), Program.cs, wwwroot/media (images & uploads)
```

## Getting started

Prerequisites: .NET 9 SDK, SQL Server (connection string in `appsettings.json`, database `WoodsyCartDb`).

1. Set the secrets (kept out of git with user-secrets):

   ```bash
   cd src/WoodsyCart.Api
   dotnet user-secrets set "Jwt:Key" "<a random string of 48+ characters>"
   dotnet user-secrets set "Seed:AdminPassword" "<initial admin password>"
   ```

   On this development machine both are already set. To see the generated admin password:

   ```bash
   dotnet user-secrets list --project src/WoodsyCart.Api
   ```

2. Run the API (migrations are applied and seed data is created on startup):

   ```bash
   dotnet run --project src/WoodsyCart.Api --launch-profile http
   ```

   - API: http://localhost:5100, Swagger: http://localhost:5100/swagger
   - Admin login: `admin@woodsycart.in` + the `Seed:AdminPassword` value. Change it from **Admin → Settings → Team & security** after first login.

3. Import the vendor catalogue: **Admin → Vendor & catalogue sync → Sync now** (takes about 5 minutes the first time).

### Seed data

On the first run the API creates:
- business settings: 15% commission, prices ending in 99, COD up to ₹25,000
- the vendor GB Furniture, with its DotPe store id for syncing
- Bengaluru pincodes (5600xx and nearby 562xxx areas)
- the coupon `WELCOME5`

## Configuration (`appsettings.json`)

| Key | Purpose |
| --- | --- |
| `ConnectionStrings:Default` | SQL Server |
| `Jwt:*` | Token issuer/audience/lifetime (`Jwt:Key` goes in user-secrets or environment variables) |
| `Cors:Origins` | Storefront and admin URLs |
| `Razorpay:KeyId`, `KeySecret`, `WebhookSecret` | Online payments. Leave empty to offer COD only. Webhook URL: `POST /api/payments/razorpay/webhook` (event `payment.captured`) |
| `Smtp:*` | Order emails. Leave `Host` empty to disable. `AdminNotifyEmail` receives new-order and enquiry alerts |
| `Vendor:AutoSyncHours` | Scheduled vendor sync interval (0 = off). Auto-sync imports new products as drafts |

In production, set secrets as environment variables, e.g. `Jwt__Key`, `Razorpay__KeySecret`, `ConnectionStrings__Default`.

## Database migrations

```bash
dotnet ef migrations add <Name> -p src/WoodsyCart.Infrastructure -s src/WoodsyCart.Api -o Data/Migrations
dotnet ef database update -p src/WoodsyCart.Infrastructure -s src/WoodsyCart.Api
```

## How pricing works

`selling price = round(vendor price × (1 + commission%))`. Commission is resolved product override → category → default.
MRP shown = max(vendor MRP, selling price). Changing the default commission, rounding, or a category's commission re-prices the affected products automatically. Products with **manual price** on are never re-priced.

## Order workflow (drop-ship)

1. Customer pays online or chooses COD → order is **Pending**, or **Confirmed** once online payment succeeds.
2. Admin confirms → **Mark placed with vendor**. A PO is created; send it with the WhatsApp button or the PO PDF.
3. Vendor shares their bill → **Add vendor bill** on the order (attach the PDF/photo). Profit switches from estimated to actual.
4. **Dispatched** (optional tracking/LR number) → **Delivered**: COD is recorded as collected and the customer invoice is generated automatically.
5. Pay the vendor from **Vendor bills**, then check **Profit & loss**.

## Deployment notes

- Host on IIS / Azure App Service / Linux (Kestrel behind nginx) with HTTPS. Update `Cors:Origins`, and `apiUrl` in both Angular apps.
- `wwwroot/media` holds downloaded product images, bills and receipts. It is git-ignored, so back it up with the database.
- QuestPDF Community licence is free for businesses under USD 1M annual revenue.
