# BDP Marketing / Email Microservice

A standalone ASP.NET Core (net8.0) minimal-API service that owns email sending for the
BDP ecosystem. It extracts the transport and branded templates that previously lived
inside `bdp-management` → `BDP.API`, with **no database dependency**, so any app
(BDP.API, the storefront, future services) can send branded email over one HTTP call.

- **Transport:** Resend HTTPS API preferred (`POST https://api.resend.com/emails`);
  SMTP/STARTTLS (MailKit, port 587, 15s timeout) as a local-dev fallback. Railway and
  most cloud hosts block outbound SMTP ports, so production must use Resend.
- **Templates:** the five branded Rhode-style (cream/serif) templates live in code
  (`EmailTemplateRegistry`) — `order_confirmation`, `order_shipped`, `invoice_sent`,
  `recurring_order_generated`, `b2b_approved` — with `{{Placeholder}}` substitution.
- **No product DB:** callers pass order line items and "Shop more" recommendation
  cards in the request; the service renders them into the branded blocks.

## Project layout

```
Marketing.Api/
  Program.cs                        # minimal API: routes, auth middleware, renderer
  Services/EmailSender.cs           # Resend / SMTP transport
  Services/EmailTemplateRegistry.cs # in-code branded templates
  Services/EmailHtmlBuilders.cs     # line-item table + recommendations block
  Models/Requests.cs                # request DTOs
Dockerfile
```

## Environment variables

| Variable | Required | Default | Purpose |
|---|---|---|---|
| `MARKETING_API_KEY` | **Yes** | — | Shared secret. Every endpoint except `/health` requires header `X-Api-Key: <this value>`. Requests are rejected with 503 if unset, 401 if wrong. |
| `Resend__ApiKey` | Production | — | Resend API key. When set, transport = `resend`. |
| `Email__FromName` | No | `BDP` | Display name on the From header. |
| `Email__FromAddress` | No | `noreply@bedifferentpackaging.com` | From address. **The domain must be verified in Resend** (bedifferentpackaging.com already is). |
| `Email__SmtpHost` | Local dev | — | SMTP fallback host. Only used when `Resend__ApiKey` is unset (transport = `smtp`). If the host contains "resend", the SMTP password is treated as a Resend API key and sends go over HTTPS anyway. |
| `Email__SmtpPort` | No | `587` | SMTP port (STARTTLS). |
| `Email__SmtpUser` | No | — | SMTP username. |
| `Email__SmtpPassword` | No | — | SMTP password. |
| `STOREFRONT_URL` | No | `https://www.bedifferentpackaging.com` | Base URL for template CTAs (`/account/orders`, `/shop`). |
| `PORT` | No | `8080` | Listen port. Railway injects this automatically. |

Transport selection: `Resend__ApiKey` set → `resend`; else `Email__SmtpHost` set →
`smtp`; else `none` (sends fail with 502 until configured).

## Endpoints

All requests except `GET /health` need the `X-Api-Key` header.

### `GET /health`

```bash
curl https://<service-url>/health
# → { "status": "ok", "transport": "resend" }   # resend | smtp | none
```

### `POST /api/email/send` — raw HTML

```bash
curl -X POST https://<service-url>/api/email/send \
  -H "X-Api-Key: $MARKETING_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "to": "customer@example.com",
    "toName": "Ivi",
    "subject": "Hello from BDP",
    "html": "<h1>Hi!</h1><p>Raw HTML body.</p>",
    "category": "adhoc"
  }'
# → { "id": "<resend-message-id>", "transport": "resend", "status": "sent" }
```

### `POST /api/email/send-template` — branded template

Loads the named template, substitutes `{{Placeholders}}` from `data` (keys are
matched case-insensitively, so camelCase JSON fills PascalCase tokens), renders
`lineItems` into the two-column line-item table and `recommendations` into the
"Shop more from Be Different" card block, and sends. Unmatched placeholders render
as empty strings so optional blocks collapse cleanly.

Notes:
- `data` values are injected as **raw HTML** — pre-escape user-supplied text, and use
  `<br>` for line breaks (e.g. in `shippingAddress`).
- Numeric `data` values whose key ends in `ZAR` are auto-formatted as `N2`
  (`12797.5` → `12,797.50`); templates prefix the `R` themselves.
- Instead of `recommendations` cards you may pass a pre-rendered
  `recommendationsHtml` string.

```bash
curl -X POST https://<service-url>/api/email/send-template \
  -H "X-Api-Key: $MARKETING_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "template": "order_confirmation",
    "to": "customer@example.com",
    "toName": "Ivi",
    "data": {
      "recipientName": "Ivi",
      "orderNumber": "SF-20260704-0042",
      "orderDate": "4 July 2026",
      "subtotalZAR": 12500.00,
      "shippingZAR": 297.50,
      "totalZAR": 12797.50,
      "shippingServiceName": "Air Standard",
      "shippingAddress": "Ivi<br>16 Beach Road<br>Cape Town, Western Cape 7139<br>South Africa"
    },
    "lineItems": [
      { "productName": "Adaeze Amber Dropper Bottle", "sku": "VL942839135338-5ML-0",
        "quantity": 2500, "unitPrice": 5.00, "lineTotal": 12500.00 }
    ],
    "recommendations": [
      { "name": "Frosted Glass Jar 50ml", "price": 8.50,
        "imageUrl": "https://www.bedifferentpackaging.com/images/jar.jpg",
        "url": "https://www.bedifferentpackaging.com/product/frosted-glass-jar-50ml" }
    ]
  }'
# → { "id": "...", "transport": "resend", "status": "sent", "template": "order_confirmation" }
```

Template keys and their main placeholders:

| Key | Placeholders |
|---|---|
| `order_confirmation` | `RecipientName`, `OrderNumber`, `OrderDate`, `LineItems`*, `SubtotalZAR`, `ShippingZAR`, `TotalZAR`, `ShippingAddress`, `ShippingServiceName`, `Recommendations`*, `StorefrontUrl` |
| `order_shipped` | `RecipientName`, `OrderNumber`, `TrackingNumber`, `TrackingCarrier`, `ShippingAddress`, `ShippingServiceName`, `StorefrontUrl` |
| `invoice_sent` | `InvoiceNumber`, `ClientName`, `TotalZAR` |
| `recurring_order_generated` | `RecipientName`, `RecurringOrderName`, `OrderNumber`, `TotalZAR` |
| `b2b_approved` | `ContactName`, `CompanyName` |

\* `LineItems` / `Recommendations` are filled from the structured `lineItems` /
`recommendations` request fields; `StorefrontUrl` defaults from the env var.

### `POST /api/email/preview-all` — eyeball every template

Renders **every** template with sample data and sends them all to one address.

```bash
curl -X POST https://<service-url>/api/email/preview-all \
  -H "X-Api-Key: $MARKETING_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{ "to": "you@example.com" }'
# → { "to": "...", "transport": "resend", "results": [ { "key": "order_confirmation", "status": "sent" }, ... ] }
```

## Error handling

- Missing/invalid `X-Api-Key` → `401`; `MARKETING_API_KEY` unset on the server → `503`.
- Validation problems (missing `to`, unknown template) → `400` / `404`.
- Provider failures (Resend non-2xx, SMTP error, transport = `none`) → `502` with
  `{ "status": "failed", "error": "<provider message>" }`.

## Local development

```bash
cd Marketing.Api
MARKETING_API_KEY=dev-key dotnet run
# http://localhost:8080/health
```

Use SMTP locally (outbound 587 usually works on dev machines) or a Resend test key.

## Deployment (Railway)

The repo has a root `Dockerfile` (sdk:8.0 build stage → aspnet:8.0 runtime). Railway
detects it automatically; the app binds `0.0.0.0:$PORT`. Set the env vars above on
the service — at minimum `MARKETING_API_KEY` and `Resend__ApiKey`.

## How BDP.API will call this (follow-up — not wired yet)

BDP.API currently sends email in-process via its `EmailService.SendAsync`. The
follow-up is to replace those call sites (order confirmation/shipped, invoices,
recurring orders, B2B approvals) with an HTTP call to this service:

1. Add env vars to the `bdp-api` Railway service:
   `MARKETING_SERVICE_URL=https://<this service>` and `MARKETING_API_KEY=<same key>`.
2. Where BDP.API built template HTML from its DB, it instead POSTs to
   `/api/email/send-template` with the template key, the placeholder `data`, its
   order `lineItems`, and product `recommendations` (BDP.API still owns the product
   DB, so it picks the recommended products and passes them as cards).
3. Attachment flows (invoice PDFs) keep using `/api/email/send` semantics — raw
   HTML plus attachment support in the transport (exposed via the API later if needed).
4. Once all call sites are migrated, the `EmailTemplates`/`EmailLogs` tables and
   SMTP/Resend env vars can be retired from BDP.API.

This repo change deliberately does **not** modify `bdp-management`.
