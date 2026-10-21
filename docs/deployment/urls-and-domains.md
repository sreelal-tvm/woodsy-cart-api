# WoodsyCart — domains and URLs to configure

> Server set-up steps: [plesk-windows-guide.md](plesk-windows-guide.md) (Plesk / Windows, the current host) or [hosting-guide.md](hosting-guide.md) (Linux + nginx).

**Main domain:** `woodsycart.com`. **Second domain:** `woodsycart.in` only redirects to `.com`.

Use this list when going live, and again whenever a domain or server changes. Tick each item as you go.

---

## 1. Addresses

| Address | What it is | Hosted from |
|---|---|---|
| `https://woodsycart.com` | Storefront (customers) | `woodsy-cart-web` build → `/var/www/woodsy-web` |
| `https://admin.woodsycart.com` | Admin app | `woodsy-cart-admin` build → `/var/www/woodsy-admin` |
| `https://api.woodsycart.com` | API, and product photos under `/media/…` | `woodsy-cart-api` on `127.0.0.1:5100` |

### Redirects (all permanent)

| From | To |
|---|---|
| `https://www.woodsycart.com/…` | `https://woodsycart.com/…` |
| `https://woodsycart.in/…` and `https://www.woodsycart.in/…` | `https://woodsycart.com/…` (same path) |
| `https://admin.woodsycart.in/…` | `https://admin.woodsycart.com/…` |
| `https://api.woodsycart.in/…` | `https://api.woodsycart.com/…` (HTTP 308, so POSTs keep working) |

On Plesk these are set in the panel (plesk-windows-guide.md, section 3); on nginx, see hosting-guide.md, section 9.

---

## 2. DNS records

Add these at the DNS provider of **each** domain. `<server IP>` is your server's public IPv4 address.

**woodsycart.com**

| Type | Name | Value | Why |
|---|---|---|---|
| A | `@` | `<server IP>` | Storefront |
| A | `www` | `<server IP>` | Redirect to `woodsycart.com` |
| A | `admin` | `<server IP>` | Admin app |
| A | `api` | `<server IP>` | API and photos |
| MX / TXT | as given by your email provider | | Mailbox and sending (section 5) |

**woodsycart.in**

| Type | Name | Value | Why |
|---|---|---|---|
| A | `@` | `<server IP>` | Redirect only |
| A | `www` | `<server IP>` | Redirect only |
| A | `admin` | `<server IP>` | Redirect only |
| A | `api` | `<server IP>` | Redirect only |

Keep the `.in` domain renewed. If it lapses, old links, printed material and old invoices stop working.

**HTTPS:** one certbot run covers all eight names (command in hosting-guide.md, section 9).

---

## 3. Storefront and admin apps (`config.json`, no rebuild)

No address is built into the apps. Each reads `config.json` (next to its `index.html`) when it starts. In the code these files hold the localhost addresses for development; on the server, edit the copies:

| File on the server | Setting | Live value | Local value (in the code) |
|---|---|---|---|
| `/var/www/woodsy-web/config.json` | `apiUrl` | `https://api.woodsycart.com` | `http://localhost:5100` |
| `/var/www/woodsy-admin/config.json` | `apiUrl` | `https://api.woodsycart.com` | `http://localhost:5100` |
| `/var/www/woodsy-admin/config.json` | `storefrontUrl` | `https://woodsycart.com` | `http://localhost:4200` |

Each upload of a new build brings back the localhost copy, so put the live files back afterwards (hosting-guide.md, section 8.1). The service worker caches API data and photos for **any** API address, so it needs no change.

---

## 4. API server settings (`appsettings.Production.json` on Plesk, `/etc/woodsycart/api.env` on Linux)

These override `appsettings.json` (which holds the localhost values for development). No rebuild is needed; restart the API after a change (`sudo systemctl restart woodsycart-api`).

| Setting | Value |
|---|---|
| `Cors__Origins__0` | `https://woodsycart.com` |
| `Cors__Origins__1` | `https://www.woodsycart.com` |
| `Cors__Origins__2` | `https://admin.woodsycart.com` |
| `Smtp__StorefrontUrl` | `https://woodsycart.com` (links in customer emails: orders, reviews, password reset) |
| `Smtp__AdminUrl` | `https://admin.woodsycart.com` (links in admin emails) |
| `Smtp__ApiUrl` | `https://api.woodsycart.com` (photo links in `sitemap.xml`) |
| `Smtp__From` | `no-reply@woodsycart.com` (fallback; Admin → Email takes priority) |
| `Seed__AdminEmail` | `admin@woodsycart.com` (used only on an empty database) |
| `Seo__StorefrontFolder` | Plesk only: `C:\Inetpub\vhosts\woodsycart.com\httpdocs` (the API writes robots.txt, sitemap.xml and link previews there) |

On Plesk, write these as JSON (`"Smtp": { "StorefrontUrl": "…" }`). A template is in [appsettings.Production.example.json](appsettings.Production.example.json).

The `.in` names are not needed in CORS, because browsers are redirected to `.com` before the apps load.

---

## 5. Email

| Where | What to set |
|---|---|
| Your email provider (Zoho Mail, Google Workspace, …) | Mailboxes such as `support@woodsycart.com` and `orders@woodsycart.com` |
| DNS of `woodsycart.com` | The provider's **MX**, **SPF** (TXT) and **DKIM** (TXT) records, and a **DMARC** record such as `v=DMARC1; p=none; rua=mailto:support@woodsycart.com`. Without these, order emails land in spam. |
| Admin → Email & notifications | Mail server, **From email** `orders@woodsycart.com`, **Reply-to** `support@woodsycart.com`, admin emails. Then press **Send test email**. |
| Admin → Business settings → **Email** | Change `support@woodsycart.in` → `support@woodsycart.com`. Shown on invoices, the Contact page and policies. |
| Optional | Forward mail sent to `@woodsycart.in` to the `.com` mailboxes (most providers can add `.in` as an alias domain). |

---

## 6. Outside services

| Service | Where | Set to |
|---|---|---|
| **Google Maps** (address picker) | Google Cloud Console → APIs & Services → Credentials → your key → Website restrictions | `https://woodsycart.com/*`, `https://www.woodsycart.com/*`, and for testing `http://localhost:4200/*` |
| **Razorpay** (when added) | Razorpay Dashboard → Settings → Webhooks | `https://api.woodsycart.com/api/payments/razorpay/webhook` (events: payment captured / failed, refund processed) |
| **Razorpay** | Dashboard → Account & Settings → Business website | `https://woodsycart.com` (with the policies pages below) |
| **UPI / bank alert forwarder** (phone app) | The app's target URL | `https://api.woodsycart.com/api/payments/upi/alert`. Copy the full URL with its `?key=` from **Admin → Payment integrations**. |
| **Instagram** profile | Edit profile → Website | `https://woodsycart.com` |
| **WhatsApp Business** profile | Settings → Business tools → Profile → Website | `https://woodsycart.com` |
| **Google Business Profile** | Edit profile → Website | `https://woodsycart.com` |
| **Google Search Console** | Add property **Domain** `woodsycart.com` (DNS TXT verification); also add `woodsycart.in` so Google sees the redirect. Then **Sitemaps** → submit | `https://woodsycart.com/sitemap.xml` |
| **Bing Webmaster Tools** (optional) | Import the site from Search Console | same sitemap |
| **GB Furniture / vendor** | Anything they link to | `https://woodsycart.com` |

---

## 7. Pages people and services link to

| Page | URL |
|---|---|
| Home | `https://woodsycart.com/` |
| Shop | `https://woodsycart.com/shop` |
| Festival offers | `https://woodsycart.com/offers` |
| Track an order | `https://woodsycart.com/track` |
| Contact | `https://woodsycart.com/contact` |
| About / FAQs | `https://woodsycart.com/about`, `https://woodsycart.com/faq` |
| Shipping, returns, privacy, terms | `https://woodsycart.com/policies/shipping`, `/policies/returns`, `/policies/privacy`, `/policies/terms` |
| Sign in / forgot password | `https://woodsycart.com/login`, `https://woodsycart.com/forgot-password` |
| Admin sign-in | `https://admin.woodsycart.com/login` |
| API health check | `https://api.woodsycart.com/api/site/settings` (should return JSON) |
| Search engines | `https://woodsycart.com/robots.txt`, `https://woodsycart.com/sitemap.xml` (built live from the catalogue, refreshed hourly) |
| Link-preview test | Facebook Sharing Debugger: `https://developers.facebook.com/tools/debug/` · Google Rich Results Test: `https://search.google.com/test/rich-results` |

---

## 8. After go-live

- [ ] `https://woodsycart.com`, `https://admin.woodsycart.com` and `https://api.woodsycart.com/api/site/settings` all open with a padlock
- [ ] `curl -I https://woodsycart.in/shop` → `301`, `location: https://woodsycart.com/shop`
- [ ] `curl -I https://www.woodsycart.com` → `301` to `https://woodsycart.com/`
- [ ] Admin → Email: test email arrives in the inbox, not spam
- [ ] An order email's **View your order** link opens on `woodsycart.com`
- [ ] Forgot-password email link opens `https://woodsycart.com/reset-password…`
- [ ] Address picker map loads on checkout (Google key restriction correct)
- [ ] A ₹1–10 payment marks the order paid (alert forwarder / Razorpay webhook correct)
- [ ] Storefront installs as an app on a phone ("Add to Home screen")

- [ ] `https://woodsycart.com/robots.txt` ends with `Sitemap: https://woodsycart.com/sitemap.xml`
- [ ] `https://woodsycart.com/sitemap.xml` lists `https://woodsycart.com/product/…` pages, and photo links start with `https://api.woodsycart.com/media/`
- [ ] Sitemap submitted in Google Search Console
- [ ] A product link pasted in WhatsApp shows photo, name and price (nginx `$social_bot` rule in hosting-guide.md, section 9)
- [ ] Rich Results Test on a product page finds **Product** (price, stock, rating) and **Breadcrumb**

**How the search files work:** the API builds them from the live catalogue: home, shop, each category, current offers, every visible product (with up to 5 photos each) and the info / policy pages. Personal pages (account, cart, checkout, orders, sign-in) are blocked in `robots.txt`. The admin app has its own `robots.txt` that blocks everything, and `api.woodsycart.com/robots.txt` allows only the photos.
