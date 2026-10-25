# WoodsyCart home server

Files for running the live shop on a Windows machine behind Cloudflare Tunnel. The step-by-step guide is the
"WoodsyCart hosting: zero to live" plan; this folder holds the files it refers to. Copy them to the server:
`Caddyfile` → `C:\WoodsyCart\caddy`, the `.ps1` scripts → `C:\WoodsyCart\scripts`.

| File | What it does | When |
| --- | --- | --- |
| `Caddyfile` | Serves the storefront on `127.0.0.1:8080` and the admin on `127.0.0.1:8081`: app routes, link previews for chat apps, cache headers | Installed once; `caddy reload` after edits |
| `appsettings.Production.example.json` | Template for `C:\WoodsyCart\api\appsettings.Production.json` (the real file holds secrets and is never committed) | Once |
| `install-services.ps1` | Creates the `WoodsyCartApi` and `Caddy` Windows services, the API's database login, and checks both answer | Once (Phase 4) |
| `install-tasks.ps1` | Schedules the nightly backup (02:30) and weekly maintenance (Sunday 03:30) | Once (Phase 8) |
| `install-qa.ps1` | Creates the QA environment (copy of live data made safe, `WoodsyCartApiQa` service, QA sites); `-RefreshData` replaces QA data with a fresh copy | Once; refresh when needed |
| `update.ps1` | Releases the current code: backup, API swap (about 30 s pause), storefront and admin; `-QA` releases to QA instead | Every release (QA first) |
| `backup.ps1` | Database backup (checked), photos, keys and settings to `H:\WoodsyCartBackups`, then Cloudflare R2 and a Healthchecks.io ping | Nightly (scheduled) |
| `maintenance.ps1` | Rebuilds indexes and statistics | Weekly (scheduled) |
| `restore-test.ps1` | Restores the newest backup as `WoodsyCartDb_test`, shows its counts, removes it | Monthly, by hand |

Machine layout:

| | Development | QA | Live |
| --- | --- | --- | --- |
| Addresses | `localhost` | `qa.` / `qaadmin.` / `qaapi.woodsycart.com` | `woodsycart.com` / `admin.` / `api.woodsycart.com` |
| Database | `WoodsyCartDb` on `DESKTOP-R1S60O8` (Developer) | `WoodsyCartDb_QA` on `DESKTOP-R1S60O8` (Developer) | `WoodsyCartDb` on `localhost\SQLEXPRESS` (Express) |
| API | `localhost:5100` (`dotnet run`) | `localhost:5300` (service `WoodsyCartApiQa`) | `localhost:5000` (service `WoodsyCartApi`) |
| Storefront / admin | `localhost:4200` / `4300` (`ng serve`) | `localhost:8090` / `8091` (service `Caddy`) | `localhost:8080` / `8081` (service `Caddy`) |
| Files | `F:\Business\GitHub` | `C:\WoodsyCart\qa` | `C:\WoodsyCart` |

Offsite backups use rclone with its settings in `C:\WoodsyCart\scripts\rclone.conf` (not in git):
`C:\WoodsyCart\tools\rclone.exe config --config C:\WoodsyCart\scripts\rclone.conf`, remote name `r2`.
The Healthchecks.io ping URL goes in `C:\WoodsyCart\scripts\healthcheck-url.txt`.
