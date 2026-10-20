# WoodsyCart — hosting guide

How to move WoodsyCart from your PC (localhost) to a real server: the API, the image cleaner, the database and both Angular apps.

- [1. What runs where](#1-what-runs-where)
- [2. Choose a server](#2-choose-a-server)
- [3. Domains and DNS](#3-domains-and-dns)
- [4. Prepare the server (Ubuntu)](#4-prepare-the-server-ubuntu)
- [5. Database](#5-database)
- [6. API](#6-api)
- [7. Image cleaner](#7-image-cleaner)
- [8. Storefront and admin apps](#8-storefront-and-admin-apps)
- [9. nginx and HTTPS](#9-nginx-and-https)
- [10. Move your existing data](#10-move-your-existing-data)
- [11. Payments after going live](#11-payments-after-going-live)
- [12. Updating the site later](#12-updating-the-site-later)
- [13. Backups](#13-backups)
- [14. Other ways to host](#14-other-ways-to-host)
- [15. Go-live checklist](#15-go-live-checklist)

---

## 1. What runs where

```
 Customer / you (browser)
        │ HTTPS
        ▼
 ┌──────────────────────────── one server ────────────────────────────┐
 │ nginx (ports 80/443)                                               │
 │   woodsycart.in        → storefront files (/var/www/woodsy-web)    │
 │   admin.woodsycart.in  → admin files      (/var/www/woodsy-admin)  │
 │   api.woodsycart.in    → API on 127.0.0.1:5100                     │
 │                                                                    │
 │ WoodsyCart API (.NET 9)          ── starts & watches ──┐           │
 │   reads/writes /var/woodsycart/media                   ▼           │
 │                                     Image cleaner (Python) on      │
 │                                     127.0.0.1:5200 (never public)  │
 │                                     reads/writes the same media    │
 │ SQL Server (WoodsyCartDb)                                          │
 └────────────────────────────────────────────────────────────────────┘
```

**The one rule for the image cleaner:** the API sends it *file paths* ("clean this file, save the result there"), not the image itself. So the cleaner must run on the **same machine** (or share the same disk at the same path) as the API.

### How the image cleaner works

1. A photo arrives: the vendor sync downloads it or you upload one, and it is saved under `media/...`.
2. If **Auto-clean** is on (Admin → Catalogue → Image clean-up), the API puts the photo in its queue. A background worker takes one photo at a time.
3. The worker calls the cleaner at `http://127.0.0.1:5200/process`. The cleaner then:
   - reads all text in the photo (RapidOCR);
   - sorts each piece of text with `cleaner/rules.py`: it **removes** vendor names, websites, prices and phone numbers, **keeps** dimensions and model codes, and marks unclear text **To review**;
   - erases the removed text: white fill on studio backgrounds, the **LaMa** AI model on rugs, wood and rooms;
   - on **studio photos**, crops off the blurred side strips and centres the product on a white 1200 × 1200 canvas; on **room photos**, only adjusts light and colour;
   - writes `{id}_c.webp` (cleaned) and `{id}_ct.webp` (thumbnail). **The original is never changed.**
4. The API points the product at the cleaned photo. Admin can approve it, switch back to the original, or re-run it at any time.

Everything runs on the CPU, offline, with no paid service. If the cleaner is not installed or stops, nothing breaks: the API restarts it automatically within about 20 seconds, and until then photos stay "Not processed" and show the vendor's original.

---

## 2. Choose a server

**Recommended:** one Linux VPS (Ubuntu 24.04 LTS) running everything.

| | Minimum | Comfortable |
|---|---|---|
| CPU | 2 vCPU | 4 vCPU |
| RAM | 4 GB | 8 GB |
| Disk | 40 GB SSD | 80 GB SSD |

- SQL Server on Linux needs **2 GB RAM by itself**. The image cleaner needs about 1 GB while working, and the API about 300 MB.
- Your media folder is currently about 1–2 GB and grows with every sync.
- Indian providers with Mumbai/Bengaluru data centres work well: DigitalOcean BLR1, AWS Lightsail Mumbai, Hostinger VPS, E2E Networks, or Azure/GCP India. Expect about ₹800–2,000 a month for 4–8 GB.

**Windows Server also works.** The steps are the same idea: IIS or the API as a Windows Service, Python for Windows, SQL Server Express. The code already handles both Windows and Linux Python paths.

**Avoid:** cheap "shared .NET hosting" with no shell access. Python can't run there, so the image cleaner won't work (see [§14](#14-other-ways-to-host)).

---

## 3. Domains and DNS

The production builds already expect these addresses (`src/environments/environment.ts` in both Angular apps):

| Address | What |
|---|---|
| `https://woodsycart.in` (and `www`) | Storefront |
| `https://admin.woodsycart.in` | Admin app |
| `https://api.woodsycart.in` | API and photos (`/media/...`) |

At your domain registrar, add **A records** for `woodsycart.in`, `www`, `admin` and `api`, all pointing to the server's IP. If you use other names, change `apiUrl` / `storefrontUrl` in both `environment.ts` files before building.

---

## 4. Prepare the server (Ubuntu)

Log in as a user with `sudo`, then run:

```bash
sudo apt update && sudo apt upgrade -y
sudo apt install -y nginx certbot python3-certbot-nginx unzip
```

**.NET 9 runtime** (ASP.NET Core):

```bash
sudo add-apt-repository -y ppa:dotnet/backports
sudo apt install -y aspnetcore-runtime-9.0
```

**Python 3.12 + venv** (for the image cleaner; Ubuntu 24.04 ships 3.12):

```bash
sudo apt install -y python3 python3-venv python3-pip libgl1 libglib2.0-0
```

**A system user and folders:**

```bash
sudo useradd --system --create-home --home-dir /opt/woodsycart woodsy
sudo mkdir -p /opt/woodsycart/api /opt/woodsycart/image-cleaner /var/woodsycart/media /var/woodsycart/keys /var/www/woodsy-web /var/www/woodsy-admin
sudo chown -R woodsy:woodsy /opt/woodsycart /var/woodsycart
```

**Firewall:** only SSH and web traffic should be open. Ports 5100, 5200 and 1433 must stay closed to the internet.

```bash
sudo ufw allow OpenSSH
sudo ufw allow 'Nginx Full'
sudo ufw enable
```

---

## 5. Database

### Option A: SQL Server on the same server (simplest)

Install SQL Server 2022 for Ubuntu ([Microsoft guide](https://learn.microsoft.com/sql/linux/quickstart-install-connect-ubuntu)) and choose the free **Express** edition (database limit 10 GB, which is plenty for years). Then:

```bash
sudo /opt/mssql/bin/mssql-conf setup     # choose Express, set a strong SA password
```

Create a login for the app, so it doesn't use `sa`:

```sql
CREATE LOGIN woodsycart WITH PASSWORD = '<strong password>';
CREATE DATABASE WoodsyCartDb;
USE WoodsyCartDb; CREATE USER woodsycart FOR LOGIN woodsycart; ALTER ROLE db_owner ADD MEMBER woodsycart;
```

### Option B: managed SQL (Azure SQL, AWS RDS)

Costs more, but backups are handled for you. Use its connection string in [§6](#6-api).

**Schema changes are automatic.** When the API starts, it applies any pending EF Core migrations. You don't need to run migration commands on the server.

To move your current products, orders and settings, see [§10](#10-move-your-existing-data).

---

## 6. API

### 6.1 Build on your PC

```bash
cd F:\Business\GitHub\woodsy-cart-api
dotnet publish src/WoodsyCart.Api -c Release -o publish/api
```

Do **not** copy `wwwroot/media` inside the publish output. Photos live in their own folder on the server ([§6.3](#63-media-folder-photos)).

Copy `publish/api` to the server's `/opt/woodsycart/api`, for example with WinSCP, or:

```bash
scp -r publish/api/* user@SERVER_IP:/tmp/api/
ssh user@SERVER_IP "sudo cp -r /tmp/api/* /opt/woodsycart/api/ && sudo chown -R woodsy:woodsy /opt/woodsycart/api"
```

### 6.2 Settings and secrets

Secrets never go in `appsettings.json` or git. On the server, put them in an environment file that only root can read:

```bash
sudo nano /etc/woodsycart/api.env      # create the folder first: sudo mkdir -p /etc/woodsycart
sudo chmod 600 /etc/woodsycart/api.env
```

```ini
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5100

ConnectionStrings__Default=Server=localhost;Database=WoodsyCartDb;User Id=woodsycart;Password=<db password>;TrustServerCertificate=True;MultipleActiveResultSets=true

# At least 32 random characters. Generate one with: openssl rand -base64 48
Jwt__Key=<long random secret>

Cors__Origins__0=https://woodsycart.in
Cors__Origins__1=https://www.woodsycart.in
Cors__Origins__2=https://admin.woodsycart.in

Smtp__Host=<smtp host>
Smtp__User=<smtp user>
Smtp__Password=<smtp password>
Smtp__AdminNotifyEmail=<your email>
Smtp__StorefrontUrl=https://woodsycart.in

# Only used the very first time, on an empty database, to create the admin login
Seed__AdminEmail=admin@woodsycart.in
Seed__AdminPassword=<strong password>

# Image cleaner (see section 7)
ImageCleaner__AutoStart=true
ImageCleaner__ToolPath=/opt/woodsycart/image-cleaner
ImageCleaner__Url=http://127.0.0.1:5200
```

- Use the **same `Jwt__Key`** forever. Changing it logs everyone out.
- Razorpay and other gateway keys are entered in **Admin → Payment integrations** and stored encrypted in the database, so they don't go here.

### 6.3 Media folder (photos)

The API serves photos from `wwwroot/media` next to the app. Point that at the permanent folder, so updates never delete photos:

```bash
sudo -u woodsy mkdir -p /opt/woodsycart/api/wwwroot
sudo -u woodsy ln -sfn /var/woodsycart/media /opt/woodsycart/api/wwwroot/media
```

### 6.4 Encryption keys (important)

`App_Data/keys` holds the keys that encrypt your saved payment-gateway secrets. **If these keys are lost, every gateway secret must be entered again.** Keep them outside the app folder too:

```bash
sudo -u woodsy mkdir -p /opt/woodsycart/api/App_Data
sudo -u woodsy ln -sfn /var/woodsycart/keys /opt/woodsycart/api/App_Data/keys
```

If you already saved gateway secrets on your PC, copy the PC's `src/WoodsyCart.Api/App_Data/keys/*` into `/var/woodsycart/keys` ([§10](#10-move-your-existing-data)).

### 6.5 Run it as a service

`/etc/systemd/system/woodsycart-api.service`:

```ini
[Unit]
Description=WoodsyCart API
After=network.target mssql-server.service

[Service]
User=woodsy
WorkingDirectory=/opt/woodsycart/api
ExecStart=/usr/bin/dotnet /opt/woodsycart/api/WoodsyCart.Api.dll
EnvironmentFile=/etc/woodsycart/api.env
Restart=always
RestartSec=5
KillSignal=SIGINT

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now woodsycart-api
sudo systemctl status woodsycart-api
journalctl -u woodsycart-api -f        # live log
curl -s http://127.0.0.1:5100/api/site/settings | head -c 200
```

> **Swagger:** the API currently shows `/swagger` in production too. It's harmless (every admin endpoint still needs a login), but you may want it turned off for live use. Ask and it can be limited to development.

---

## 7. Image cleaner

You don't create a service for the cleaner: **the API starts it, watches it, and restarts it if it stops** (because `ImageCleaner__AutoStart=true`). You only install it once.

### 7.1 Copy the tool

Copy `woodsy-cart-api/tools/image-cleaner` to `/opt/woodsycart/image-cleaner`, **without** its `.venv` folder (the Windows virtual environment doesn't work on Linux):

- `server.py`, `scan.py`, `requirements.txt`, `cleaner/`
- `models/lama_fp32.onnx` (208 MB). Copy it from your PC, or download it again from <https://huggingface.co/Carve/LaMa-ONNX> (Apache-2.0).

### 7.2 Install

```bash
cd /opt/woodsycart/image-cleaner
sudo -u woodsy python3 -m venv .venv
sudo -u woodsy .venv/bin/pip install -r requirements.txt
```

### 7.3 Test it by hand once

```bash
sudo -u woodsy .venv/bin/python server.py &
sleep 20; curl -s http://127.0.0.1:5200/health        # → {"ok":true,"inpaint":"lama"}
kill %1
```

`"inpaint":"opencv"` means the LaMa model file is missing. The tool still works, but repairs on textured backgrounds look blurrier.

### 7.4 Let the API run it

```bash
sudo systemctl restart woodsycart-api
journalctl -u woodsycart-api -n 50 | grep -i cleaner      # "Image cleaner started (pid …)"
```

In **Admin → Catalogue → Image clean-up** the status should show **Online · LaMa**.

### Notes

- The cleaner only listens on `127.0.0.1:5200`, and it has no login. **Never open port 5200** or proxy it through nginx.
- **Load:** about 1 s per photo, or about 10 s when LaMa repairs an area, one photo at a time. A daily vendor sync of a few dozen new photos takes a minute or two.
- **Photos you already cleaned are just files** in `media`. Copy the media folder and the database ([§10](#10-move-your-existing-data)), and nothing needs cleaning again.
- To change what is removed or kept, edit `cleaner/rules.py` and restart the API.

---

## 8. Storefront and admin apps

Build both on your PC:

```bash
cd F:\Business\GitHub\woodsy-cart-web
npx ng build
cd F:\Business\GitHub\woodsy-cart-admin
npx ng build
```

The output is in `dist/woodsy-cart-web/browser` and `dist/woodsy-cart-admin/browser`. Copy their **contents** to:

- `/var/www/woodsy-web/` (storefront)
- `/var/www/woodsy-admin/` (admin)

Both apps are PWAs with service workers, which **only work over HTTPS**. Section 9 sets that up.

---

## 9. nginx and HTTPS

`/etc/nginx/sites-available/woodsycart`:

```nginx
# Storefront
server {
    server_name woodsycart.in www.woodsycart.in;
    root /var/www/woodsy-web;
    index index.html;

    location / { try_files $uri $uri/ /index.html; }               # Angular routes
    location = /index.html  { add_header Cache-Control "no-cache"; }
    location = /ngsw.json   { add_header Cache-Control "no-cache"; }  # lets the PWA see updates
    location ~* \.(js|css|woff2?|png|jpg|svg|webp|ico)$ { expires 30d; add_header Cache-Control "public"; }
}

# Admin
server {
    server_name admin.woodsycart.in;
    root /var/www/woodsy-admin;
    index index.html;

    location / { try_files $uri $uri/ /index.html; }
    location = /index.html  { add_header Cache-Control "no-cache"; }
    location = /ngsw.json   { add_header Cache-Control "no-cache"; }
    location ~* \.(js|css|woff2?|png|jpg|svg|webp|ico)$ { expires 30d; add_header Cache-Control "public"; }
}

# API (+ photos under /media)
server {
    server_name api.woodsycart.in;
    client_max_body_size 25m;                       # photo uploads from admin

    location /media/ {                              # serve photos straight from disk (fast)
        alias /var/woodsycart/media/;
        expires 30d;
        add_header Cache-Control "public";
    }

    location / {
        proxy_pass         http://127.0.0.1:5100;
        proxy_http_version 1.1;
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_read_timeout 300s;                    # long admin jobs (vendor sync)
    }
}
```

```bash
sudo ln -s /etc/nginx/sites-available/woodsycart /etc/nginx/sites-enabled/
sudo rm -f /etc/nginx/sites-enabled/default
sudo nginx -t && sudo systemctl reload nginx

# Free HTTPS certificates (renew automatically)
sudo certbot --nginx -d woodsycart.in -d www.woodsycart.in -d admin.woodsycart.in -d api.woodsycart.in
```

Open `https://woodsycart.in` and `https://admin.woodsycart.in` and log in.

---

## 10. Move your existing data

Do this once, before go-live, so the server starts with all imported products, cleaned photos and settings.

**1. Database:** back up on your PC, then restore on the server.

```sql
-- On your PC (SSMS or sqlcmd)
BACKUP DATABASE WoodsyCartDb TO DISK = 'F:\Backup\WoodsyCartDb.bak' WITH COPY_ONLY, INIT;
```

Copy the `.bak` file to the server (for example to `/var/opt/mssql/backup/`, owned by `mssql`), then run:

```sql
RESTORE DATABASE WoodsyCartDb FROM DISK = '/var/opt/mssql/backup/WoodsyCartDb.bak'
WITH MOVE 'WoodsyCartDb' TO '/var/opt/mssql/data/WoodsyCartDb.mdf',
     MOVE 'WoodsyCartDb_log' TO '/var/opt/mssql/data/WoodsyCartDb_log.ldf', REPLACE;
-- Check the logical file names first with: RESTORE FILELISTONLY FROM DISK = '...'
USE WoodsyCartDb; CREATE USER woodsycart FOR LOGIN woodsycart; ALTER ROLE db_owner ADD MEMBER woodsycart;
```

**2. Photos:** copy everything in `woodsy-cart-api/src/WoodsyCart.Api/wwwroot/media/` to `/var/woodsycart/media/`. This includes the originals and the `_c` / `_ct` cleaned files.

```bash
sudo chown -R woodsy:woodsy /var/woodsycart/media
```

**3. Encryption keys:** copy `woodsy-cart-api/src/WoodsyCart.Api/App_Data/keys/*` to `/var/woodsycart/keys/`.

**4. Clean up test data:** remove test orders, customers and payment alerts from the database before customers arrive.

**5.** Restart the API with `sudo systemctl restart woodsycart-api`.

---

## 11. Payments after going live

In **Admin → Payment integrations**:

- **Direct UPI:** enter your business UPI ID and payee name.
- **Payment alert forwarder** (the app on your phone that forwards bank or UPI SMS): change its target to
  `https://api.woodsycart.in/api/payments/upi/alert`, keeping the same alert key.
- **Razorpay** (when you add it): set its webhook in the Razorpay dashboard to
  `https://api.woodsycart.in/api/payments/razorpay/webhook`, and enter its keys in Admin.

Place one small real order end to end (pay ₹1–10) before announcing the site.

---

## 12. Updating the site later

| What changed | Do this |
|---|---|
| API code | `dotnet publish …`, copy to `/opt/woodsycart/api` (the `media` and `keys` links stay), then `sudo systemctl restart woodsycart-api`. Database changes apply automatically on start. |
| Storefront / admin | `npx ng build`, then replace the contents of `/var/www/woodsy-web` or `/var/www/woodsy-admin`. Open PWAs update themselves on the next visit. |
| Image-cleaner rules | Copy the changed files in `cleaner/` and restart the API. To apply new rules to existing photos: Admin → Image clean-up → **Re-run all**. |
| Python packages | `sudo -u woodsy /opt/woodsycart/image-cleaner/.venv/bin/pip install -r requirements.txt`, then restart the API. |

---

## 13. Backups

Back up these three, **daily**, to somewhere off the server (another provider, Google Drive, S3 or Backblaze B2):

1. **Database**: a nightly `BACKUP DATABASE` (cron + `sqlcmd`). Keep about 14 days.
2. **`/var/woodsycart/media`**: all photos and invoices. `rclone sync` copies only changes.
3. **`/var/woodsycart/keys`**: small, but critical for payment secrets.

Also keep a copy of `/etc/woodsycart/api.env` (your secrets) somewhere private, such as a password manager.

Example nightly database backup (`sudo crontab -e`):

```cron
30 2 * * * /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P '<sa password>' -C -Q "BACKUP DATABASE WoodsyCartDb TO DISK='/var/opt/mssql/backup/WoodsyCartDb-$(date +\%a).bak' WITH INIT"
```

---

## 14. Other ways to host

### Docker (two containers)

Use this if your host prefers containers.

- **api** container: the published API. Set `ImageCleaner__AutoStart=false` and `ImageCleaner__Url=http://image-cleaner:5200`.
- **image-cleaner** container: `python:3.12-slim` plus `requirements.txt`, running `python server.py` with `IMAGE_CLEANER_PORT=5200`. The server must listen on `0.0.0.0` inside Docker; that needs a one-line change in `server.py`.
- **Mount the same media volume at the same path** in both containers (for example `/app/wwwroot/media`), because file paths are passed between them.
- Don't publish port 5200 to the host.

### Hosting without Python (shared .NET hosting, some app services)

- The API and shop work normally. In Admin, Image clean-up shows **Offline**, and new photos keep the vendor's original.
- Turn **Auto-clean** off in Admin to stop new photos being queued.
- To still clean photos, the API would need a small change so it sends the image itself, letting the cleaner run on another machine (even your PC). Ask for this if you choose such a host.

### Cloud photo storage later (S3, Azure Blob, Cloudflare R2)

That needs the same small change (send images instead of file paths), plus moving the `media` URLs to the storage address.

---

## 15. Go-live checklist

- [ ] DNS A records for `woodsycart.in`, `www`, `admin` and `api` point to the server
- [ ] `https://` works on all four, and the certificate auto-renews (`sudo certbot renew --dry-run`)
- [ ] `/etc/woodsycart/api.env` filled in: DB, `Jwt__Key`, CORS origins, SMTP, admin seed
- [ ] Database restored, and test orders and customers removed
- [ ] `/var/woodsycart/media` and `/var/woodsycart/keys` copied, and the links in the API folder created
- [ ] `systemctl status woodsycart-api` is **active**, and the API restarts after a server reboot
- [ ] Admin → Image clean-up shows **Online · LaMa**
- [ ] Admin → Payment integrations: UPI ID set; alert forwarder points to the new API URL
- [ ] One real ₹1–10 order paid and confirmed end to end
- [ ] Order and invoice emails arrive (SMTP)
- [ ] Firewall: only 22, 80 and 443 open (`sudo ufw status`)
- [ ] Nightly backups of the database, media and keys running, and one restore tested
- [ ] Admin password changed from the seed password
