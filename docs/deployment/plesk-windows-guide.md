# WoodsyCart — deploying on Plesk (Windows / IIS)

For hosting at **win26.plreseller.com** (Plesk on Windows, IIS, Microsoft SQL Server). If you ever move to a Linux server with nginx, use [hosting-guide.md](hosting-guide.md) instead. Every address used below is listed in [urls-and-domains.md](urls-and-domains.md).

---

## 1. What goes where

All three sites live in one Plesk subscription (`woodsycart.com`):

| Site | Address | Folder on the server (check in Plesk → Hosting Settings → *Document root*) | What you upload |
|---|---|---|---|
| Storefront | `https://woodsycart.com` | `C:\Inetpub\vhosts\woodsycart.com\httpdocs` | `woodsy-cart-web\dist\woodsy-cart-web\browser\*` |
| Admin | `https://admin.woodsycart.com` | `C:\Inetpub\vhosts\woodsycart.com\admin.woodsycart.com` | `woodsy-cart-admin\dist\woodsy-cart-admin\browser\*` |
| API | `https://api.woodsycart.com` | `C:\Inetpub\vhosts\woodsycart.com\api.woodsycart.com` | `publish\*` (step 5) |
| Database | `mssql.woodsycart.com` (Plesk shows the exact address) | Plesk → Databases | your data (step 4) |

Files that come **with every build** and set up IIS for you:

| File | Does |
|---|---|
| storefront `web.config` | Opens the app for any page address, sends WhatsApp / Facebook preview bots to ready-made preview pages, sets caching and file types |
| admin `web.config` | Opens the app for any page address, keeps the admin out of Google, sets caching |
| API `web.config` | Runs the API in IIS, allows uploads up to 25 MB, turns off WebDAV (it blocks the admin's save and delete buttons) |

Files that exist **only on the server** (never overwritten by an upload):

| File | Holds |
|---|---|
| `httpdocs\config.json`, `admin.woodsycart.com\config.json` | The live API address for the two apps |
| `api.woodsycart.com\appsettings.Production.json` | Database password, JWT key, live addresses |
| `api.woodsycart.com\App_Data\keys\` | Encryption keys for payment secrets |
| `api.woodsycart.com\wwwroot\media\` | All product photos, bills and receipts |

---

## 2. Ask your host first (one message)

Send this to plreseller support and wait for a "yes" to each:

1. Is the **ASP.NET Core 9 Hosting Bundle** (ASP.NET Core Module V2 + .NET 9 runtime) installed on win26? If only the module is installed, that's fine too; tell me.
2. Is the IIS **URL Rewrite** module installed?
3. Which **SQL Server version** does the server run (2019 / 2022 / 2025)? Can I connect to it **remotely** with SQL Server Management Studio (port 1433)?
4. Can my subscription have a **dedicated application pool** with **idle time-out = 0** (or "Always running")? The API runs background jobs (vendor sync, emails, sitemap).
5. Please give the application pool user **write access** to my `httpdocs` folder and the `api.woodsycart.com` folder.

If (1) is "no", publish the API **self-contained** (step 5, second command). If (4) is "no", use the free uptime monitor in step 9.

---

## 3. Plesk set-up (once)

### 3.1 Subdomains
**Websites & Domains → Add Subdomain**, twice:

| Subdomain | Document root |
|---|---|
| `admin` | `admin.woodsycart.com` |
| `api` | `api.woodsycart.com` |

Plesk adds the DNS records (`admin` and `api` → 178.63.101.250) automatically. **Do not** also add them by hand in DNS Settings.

### 3.2 HTTPS
For **each** of `woodsycart.com`, `admin.woodsycart.com` and `api.woodsycart.com`:
1. **SSL/TLS Certificates → Install** a free **Let's Encrypt** certificate. For `woodsycart.com` also tick *Include www* and *Secure webmail / mail*.
2. **Hosting Settings** → tick **Permanent SEO-safe 301 redirect from HTTP to HTTPS**.

For `woodsycart.com` only: **Hosting Settings → Preferred domain** = `woodsycart.com` (non-www). Plesk then redirects `www.woodsycart.com` → `woodsycart.com`.

### 3.3 The `.in` domain → `.com`
1. At the registrar of `woodsycart.in`, set the name servers to `ns261.plreseller.com` and `ns262.plreseller.com`.
2. Plesk → `woodsycart.com` → **Add Domain Alias** → `woodsycart.in`. Tick **Redirect with the HTTP 301 code**, plus *Mail service* and *Web service*.
3. Issue a Let's Encrypt certificate that includes `woodsycart.in` and `www.woodsycart.in`. Without it, `https://woodsycart.in` shows a warning before redirecting.

### 3.4 Application pool
**Websites & Domains → Dedicated IIS Application Pool** (the name differs slightly between Plesk versions):
- Switch it **on** for the subscription.
- **Idle time-out: 0** (never stop), and Recycling at a fixed night time (e.g. 04:00) instead of every 29 hours.
- .NET CLR version doesn't matter for ASP.NET Core ("No managed code" or v4 both work).

### 3.5 Write permissions
**Hosting Settings** of `woodsycart.com` **and** of `api.woodsycart.com` → tick **Additional write/modify permissions**. The API needs to:
- save uploaded photos in `api.woodsycart.com\wwwroot\media` and keys in `App_Data`;
- write `robots.txt`, `sitemap.xml` and `share\…` preview pages into `httpdocs` every hour.

---

## 4. Database

### 4.1 Create it
**Databases → Add Database**:
- Type **Microsoft SQL Server**; name e.g. `woodsycart`.
- Create a database user (e.g. `woodsycart_app`) with a strong password and **read/write + DDL** rights (Plesk's default "Read and write" user is fine).
- Note the **database server address** Plesk shows (e.g. `mssql.woodsycart.com` or `localhost\MSSQLSERVER2022`). The connection string uses exactly that.

### 4.2 Bring your existing data over
Your PC runs **SQL Server 2025 (v17)**. A `.bak` backup restores **only on the same or a newer** version, so pick the option that matches the host's answer (step 2, question 3).

**Option A: host runs SQL Server 2025 → `.bak`**
1. On your PC (Command Prompt):
   ```bat
   sqlcmd -S DESKTOP-R1S60O8 -E -C -Q "BACKUP DATABASE WoodsyCartDb TO DISK='C:\Program Files\Microsoft SQL Server\MSSQL17.MSSQLSERVER\MSSQL\Backup\WoodsyCartDb.bak' WITH COPY_ONLY, INIT, COMPRESSION"
   ```
2. Zip the `.bak`. In Plesk → **Databases → woodsycart → Import Dump**, upload the zip.

**Option B: host runs an older version → BACPAC (recommended)**
1. Install **SqlPackage** on your PC (Microsoft's free tool: `dotnet tool install -g microsoft.sqlpackage`).
2. Export your local database:
   ```bat
   sqlpackage /Action:Export /SourceServerName:DESKTOP-R1S60O8 /SourceDatabaseName:WoodsyCartDb /SourceTrustServerCertificate:True /TargetFile:C:\Temp\WoodsyCartDb.bacpac
   ```
3. Import it into the **empty** Plesk database (needs remote access, step 2, question 3):
   ```bat
   sqlpackage /Action:Import /SourceFile:C:\Temp\WoodsyCartDb.bacpac /TargetServerName:<database server address> /TargetDatabaseName:woodsycart /TargetUser:woodsycart_app /TargetPassword:<password> /TargetTrustServerCertificate:True
   ```
   If the host doesn't allow remote access, ask support to import the `.bacpac` for you.

**Option C: start empty.** Skip this step. The API creates all tables on its first start and makes the admin login from `Seed:AdminPassword`. You'd then re-import products from the vendor in Admin.

**Before go-live, remove test data** in Admin: any orders, customers or reviews you created yourself while trying the shop.

---

## 5. Build on your PC

```bat
cd F:\Business\GitHub\woodsy-cart-web
npx ng build

cd F:\Business\GitHub\woodsy-cart-admin
npx ng build

cd F:\Business\GitHub\woodsy-cart-api
dotnet publish src\WoodsyCart.Api -c Release -r win-x64 --self-contained false -o publish
```

If the host has **no .NET 9 runtime** (step 2, question 1), publish self-contained instead (about 100 MB larger):
```bat
dotnet publish src\WoodsyCart.Api -c Release -r win-x64 --self-contained true -o publish
```

The publish folder never contains your local secrets, photos or keys; the project file excludes them.

**If you also run the API locally:** publishing for `win-x64` rebuilds the shared build files, and a local API that's still running can then fail with *"Something went wrong"* (log: *Could not load file or assembly 'Microsoft.IdentityModel.Abstractions'*). Stop the local API before publishing. Afterwards run `dotnet build` once before starting it again.

---

## 6. Upload the API (`api.woodsycart.com`)

Use **Plesk → Files** (upload a zip, then *Extract*) or FTP.

1. Upload everything inside `publish\` to the API folder.
2. Create **`appsettings.Production.json`** next to `WoodsyCart.Api.dll`. Copy [appsettings.Production.example.json](appsettings.Production.example.json) and fill in:
   - the connection string (address, database, user, password from step 4.1);
   - `Jwt:Key`: a new long random value. If you keep the local key instead, everyone stays signed in after the move; a new key signs everyone out once.
   - `Seo:StorefrontFolder`: the storefront's document root from step 1.
3. Upload your local photos: zip `F:\Business\GitHub\woodsy-cart-api\src\WoodsyCart.Api\wwwroot\media` (about 250 MB) and extract it so it becomes `api.woodsycart.com\wwwroot\media\products\…`.
4. Upload your encryption keys: `src\WoodsyCart.Api\App_Data\keys\*.xml` → `api.woodsycart.com\App_Data\keys\`. Without them, payment gateway secrets saved in Admin can't be read, and you'd re-enter them.
5. Open `https://api.woodsycart.com/api/site/settings`. You should see JSON with your business name.

If you see an error page instead:

| Error | Usually means | Fix |
|---|---|---|
| **HTTP 500.19** | IIS doesn't understand `web.config` | URL Rewrite or ASP.NET Core Module missing → ask the host (step 2) |
| **HTTP 500.30 / 500.31** | The API started and stopped | Wrong connection string, missing `Jwt:Key`, or no .NET 9 runtime (publish self-contained). To see the reason: in `web.config` set `stdoutLogEnabled="true"`, create a `logs` folder, reload, read `logs\stdout_*.log`, then set it back to `false`. |
| **HTTP 502.5** | Same as 500.30 on older module versions | Same as above |

---

## 7. Upload the storefront and admin

**Storefront** (`httpdocs`):
1. Upload everything inside `woodsy-cart-web\dist\woodsy-cart-web\browser\`. That includes `web.config`; keep it.
2. Replace `httpdocs\config.json` with:
   ```json
   { "apiUrl": "https://api.woodsycart.com" }
   ```
3. Within an hour (or after the API restarts) the API writes `robots.txt`, `sitemap.xml` and the `share\` folder into `httpdocs`. Don't delete them.

**Admin** (`admin.woodsycart.com`):
1. Upload everything inside `woodsy-cart-admin\dist\woodsy-cart-admin\browser\`, including `web.config`.
2. Replace its `config.json` with:
   ```json
   { "apiUrl": "https://api.woodsycart.com", "storefrontUrl": "https://woodsycart.com" }
   ```

**Tip:** keep the two live `config.json` files in a folder on your PC (e.g. `F:\Business\woodsycart-live\`). Re-upload them after every app update, because each build carries the localhost copy.

Open `https://woodsycart.com` and `https://admin.woodsycart.com`. Sign in to the admin, then open a product page and press **F5** (it must reload, not show 404).

---

## 8. Email, payments, Google

1. **Mailbox:** Plesk → **Mail → Create Email Address** → `orders@woodsycart.com` (and `support@woodsycart.com` if you want).
2. **Admin → Email & notifications:**
   - **Host** `mail.woodsycart.com`, **Port** 587, STARTTLS on;
   - **User** `orders@woodsycart.com` and its password; **From** `orders@woodsycart.com`;
   - then **Send test email**.

   Your DNS already has SPF, DKIM and DMARC for this server, so mail sent this way reaches inboxes.
3. **Admin → Business settings:** change the email to `support@woodsycart.com`.
4. **Admin → Payment integrations:** UPI ID. Point the UPI alert forwarder app at `https://api.woodsycart.com/api/payments/upi/alert?key=…` (copy it from that page).
5. **Google Maps key:** website restrictions `https://woodsycart.com/*` and `https://www.woodsycart.com/*`.
6. **Google Search Console:** add `woodsycart.com` (DNS TXT record via Plesk → DNS Settings → Add Record → TXT), then submit `https://woodsycart.com/sitemap.xml`.

---

## 9. Keep the API awake

IIS stops sites after a quiet period unless the application pool idle time-out is 0 (step 3.4). A stopped API means:
- no vendor sync;
- emails wait until someone visits;
- the sitemap isn't refreshed.

If the host can't set idle time-out 0, add a free monitor (e.g. **UptimeRobot**, HTTP check every 5 minutes) on `https://api.woodsycart.com/api/site/settings`. It keeps the API running and emails you if the site goes down.

**Image cleaner:** the automatic watermark/text remover needs Python, which shared Windows hosting doesn't offer, so it's switched off (`ImageCleaner:AutoStart: false`). Photos still work. You can clean photos on your PC before uploading, or move the API to a VPS later.

---

## 10. Updating later

**Storefront / admin:** build, then upload the new `browser\*` files over the old ones. Re-upload your live `config.json`. Visitors get the new version on their next visit; installed apps update themselves.

**API:**
1. Upload a file named **`app_offline.htm`** (any short text, e.g. "Updating, back in a minute") into the API folder. IIS stops the API and releases its files.
2. Upload the new `publish\*` files. `appsettings.Production.json`, `App_Data` and `wwwroot\media` aren't in the publish folder, so they stay untouched.
3. Delete `app_offline.htm`. The API starts and applies any database changes automatically.

---

## 11. Backups

- **Plesk → Backup Manager:** schedule a daily backup of the subscription (files + databases + mail) and keep at least 7. Store a copy off the server too (Plesk can send it to FTP / Google Drive / OneDrive, depending on the plan).
- Most important, in order: the **database**, `api.woodsycart.com\wwwroot\media`, `api.woodsycart.com\App_Data\keys`, `appsettings.Production.json`.
- Restore-test once: download a backup and restore it into a scratch database.

---

## 12. Troubleshooting

| What you see | Cause | Fix |
|---|---|---|
| Page refresh shows **404** on the storefront/admin | `web.config` missing or URL Rewrite not installed | Re-upload `web.config` from the build; ask the host for URL Rewrite |
| Admin **Save / Delete** fails with **405** | WebDAV is intercepting PUT/DELETE | Make sure the API's `web.config` is the one from the publish folder (it removes WebDAV) |
| Browser console: **CORS** error | `Cors:Origins` doesn't list the site address exactly | Fix `appsettings.Production.json` (with `https://`, no trailing `/`) and restart the API |
| Product photos missing | `wwwroot\media` not uploaded, or wrong folder level | Must be `api.woodsycart.com\wwwroot\media\products\…` |
| Photo upload fails | No write permission | Step 3.5 |
| `sitemap.xml` / `robots.txt` missing or show `localhost` | `Seo:StorefrontFolder` or `Smtp:StorefrontUrl` / `ApiUrl` not set, or no write permission on `httpdocs` | Fix settings, step 3.5, restart the API, wait a minute |
| WhatsApp preview shows only "WoodsyCart" | The `share\product\…` page doesn't exist yet, or WhatsApp cached the link | Check `httpdocs\share\product\<slug>.html` exists; test in Facebook's Sharing Debugger and press *Scrape Again* |
| Emails don't arrive | SMTP settings, or the API was asleep | Admin → Email → Send test email; step 9 |
| Payment secrets "can't be read" after the move | `App_Data\keys` not copied | Upload the keys from your PC (step 6.4) or re-enter the secrets |

**Restart the API:** Plesk → `api.woodsycart.com` → **Dedicated IIS Application Pool → Recycle** (or upload and delete `app_offline.htm`).

---

## 13. Go-live checklist

- [ ] Host confirmed: ASP.NET Core 9 (or self-contained publish), URL Rewrite, SQL Server version, app pool idle time-out 0, write permissions
- [ ] Subdomains `admin` and `api` created; Let's Encrypt on all three (+ www, + `woodsycart.in`); HTTP→HTTPS redirects on
- [ ] `www.woodsycart.com` and `woodsycart.in` redirect to `https://woodsycart.com`
- [ ] Database created and data imported; test data removed
- [ ] API: `appsettings.Production.json` filled in, `App_Data\keys` and `wwwroot\media` uploaded; `/api/site/settings` returns JSON
- [ ] Storefront and admin uploaded with their live `config.json`; F5 on a product page works
- [ ] `https://woodsycart.com/robots.txt` and `/sitemap.xml` show `woodsycart.com` addresses
- [ ] A product link pasted in WhatsApp shows photo, name and price
- [ ] Admin → Email: test email arrives; order emails link to `woodsycart.com`
- [ ] UPI ID set; alert forwarder points at `api.woodsycart.com`; a ₹1–10 test order goes through end to end
- [ ] Google Maps key restricted to `woodsycart.com`; sitemap submitted in Search Console
- [ ] Backups scheduled in Plesk; uptime monitor on the API
- [ ] Admin password changed from the seed password
