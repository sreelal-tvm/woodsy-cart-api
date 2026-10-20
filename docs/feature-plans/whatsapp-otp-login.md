# Feature plan: Phone number + WhatsApp OTP login

**Status:** Planned, waiting for the WhatsApp Business Platform (Meta Cloud API) setup.
**Affects:** woodsy-cart-api, woodsy-cart-web (storefront), woodsy-cart-admin.

## Goal

Customers sign up and sign in with their mobile number. A one-time code is sent on WhatsApp, with a backup channel for anyone who doesn't receive it. The same WhatsApp Business number is used for customer chat, offers and OTPs.

## Decisions

- **Channel:** WhatsApp authentication template sent through **Meta's Cloud API directly**. No reseller, so no platform fee.
  - India rate: about ₹0.115 per OTP.
  - No DLT registration is needed for WhatsApp.
- **Same number for app and API (coexistence):** the number keeps working in the WhatsApp Business phone app for chats, while the website sends OTPs through the API.
  - Trade-off: once coexistence is on, new broadcast lists can't be created in the app. Promotions then go through the API at about ₹0.86 per marketing message, to opted-in customers only.
  - Alternative if free app broadcasts must stay: use a second SIM only for API/OTP.
- **Backups:**
  - Email OTP: free, via SMTP (for example Brevo's free plan, 300 emails a day).
  - Later, SMS OTP: either a provider that sends under its own DLT registration, or MSG91/2Factor once WoodsyCart is DLT-registered (₹5,900 one-time).
- **Rejected options:**
  - Firebase phone auth: about ₹5–6 per OTP in India.
  - Personal-SIM SMS gateways: risk under TRAI's unregistered-telemarketer rules (disconnection and blacklisting).

## Business owner setup (prerequisites)

1. Use the number in the **WhatsApp Business app** with the WoodsyCart profile for at least 7 days (1–2 months recommended).
2. Create a **Meta Business Portfolio** (business.facebook.com).
3. **Business verification.** The account works without it, capped at 250 unique customers per 24 hours. Verification raises the cap to 1,000–2,000 or more. Needs a GST, Udyam or shop licence, and a website on a domain matching the business name.
4. In developers.facebook.com, create an app, add the **WhatsApp** product, and onboard the existing number with **coexistence**.
5. Get the **display name "WoodsyCart"** approved.
6. Add a **payment method** in Meta Business.
7. Create an **Authentication** template with a **Copy code** button (code variable plus optional expiry note).
8. Provide the following:
   - Phone Number ID
   - WhatsApp Business Account ID
   - Permanent System User access token, kept in user-secrets or environment variables, never in git
   - Template name and language

## API changes (woodsy-cart-api)

- Config section `WhatsApp`: `PhoneNumberId`, `BusinessAccountId`, `AccessToken`, `OtpTemplateName`, `OtpTemplateLanguage`, `GraphApiVersion`.
- `IOtpSender` abstraction with these implementations:
  - `WhatsAppOtpSender`: Graph API `POST /{phone-number-id}/messages`, type `template`, body parameter plus copy-code button parameter.
  - `EmailOtpSender`: uses the existing `EmailSender`.
  - `SmsOtpSender`: provider chosen in settings; added later.
- New entity `OtpChallenge`: `Id`, `Phone`, `CodeHash` (salted hash, never the plain code), `Channel`, `Purpose`, `Attempts`, `ExpiresAt`, `ConsumedAt`, `IpAddress`, `CreatedAt`.
- New fields on `AppUser`:
  - `PhoneVerified` (bool)
  - unique index on normalised `Phone` (E.164, `+91XXXXXXXXXX`)
  - `WhatsAppOptIn` (bool) + `WhatsAppOptInAt`
- Endpoints:
  - `POST /api/auth/otp/request { phone, channel? }`: normalises the number, applies rate limits, sends the code. Returns `{ challengeId, channel, resendAfterSeconds }`.
  - `POST /api/auth/otp/verify { challengeId, code, fullName?, email? }`:
    - verifies the code;
    - signs in the existing user with that phone, or creates a new customer;
    - if the phone matches a guest record, upgrades it so guest orders carry over.
    - Returns `AuthResponse`.
  - `POST /api/auth/otp/fallback { challengeId, channel: "email" | "sms" }`: resends on the backup channel.
- Rules:
  - 6-digit code, valid 5 minutes, at most 5 wrong attempts per challenge.
  - Resend after 30 seconds; at most 5 OTPs per phone per hour and 10 per IP per hour (ASP.NET Core rate limiter plus database checks).
  - Customer tokens stay valid 30 days, so customers rarely need a new OTP.
  - Log every send (channel, cost category) for monthly cost reporting.
- Meta webhook (optional): `POST /api/whatsapp/webhook` for delivery status, so the app can fall back automatically when a message fails.

## Storefront changes (woodsy-cart-web)

- Login and register become one screen: enter mobile number, then enter the code (6-digit input with autofill and paste support).
  - "Didn't get it? Send by email" after 30 seconds.
  - Email + password and Google sign-in stay available as alternatives.
- New customers are asked only for their name (and optionally an email for invoices).
- Checkout: guests can verify their phone inline. The "Send me offers on WhatsApp" opt-in checkbox appears at sign-up and checkout, unticked by default.
- Profile: shows the verified phone, change number (OTP on the new number), and WhatsApp offers on/off.

## Admin changes (woodsy-cart-admin)

- Settings, Login & OTP tab:
  - enable phone login;
  - primary channel and backup channel;
  - WhatsApp connection test (sends a test OTP to the admin's number);
  - month-to-date OTP count and estimated cost.
- Customers: phone-verified badge, WhatsApp opt-in status, and an export of opted-in customers for campaigns.

## Later (optional)

- WhatsApp order updates (confirmed, dispatched, delivered) as utility templates, about ₹0.115 each.
- WhatsApp promotions to opted-in customers from the admin app, as marketing templates at about ₹0.86 each.

## Cost estimate

| Item | Monthly volume | Approx. cost |
|---|---|---|
| WhatsApp OTP | 300 | ₹35 |
| Email OTP backup | any (≤ 300/day) | ₹0 |
| WhatsApp promotions (opted-in) | 1,000 | ₹860 |
| Customer-initiated chats (24 h window) | any | ₹0 |

Rates are from Meta's July 2026 India rate card as reported by third parties. Confirm them in Meta Business before launch.
