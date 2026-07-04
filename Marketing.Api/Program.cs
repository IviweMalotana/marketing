using System.Security.Cryptography;
using System.Text;
using Marketing.Api.Models;
using Marketing.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Railway injects PORT; bind all interfaces so the edge proxy can reach us.
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddHttpClient();
builder.Services.AddSingleton<EmailSender>();

var app = builder.Build();

var storefrontUrl =
    Environment.GetEnvironmentVariable("STOREFRONT_URL")
    ?? app.Configuration["StorefrontUrl"]
    ?? "https://www.bedifferentpackaging.com";

// ── Auth: every route except /health requires X-Api-Key == MARKETING_API_KEY ──

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    var expected = Environment.GetEnvironmentVariable("MARKETING_API_KEY")
                   ?? ctx.RequestServices.GetRequiredService<IConfiguration>()["MARKETING_API_KEY"];
    if (string.IsNullOrEmpty(expected))
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsJsonAsync(new { message = "MARKETING_API_KEY is not configured on the server." });
        return;
    }

    var provided = ctx.Request.Headers["X-Api-Key"].FirstOrDefault() ?? "";
    if (!FixedTimeEquals(provided, expected))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { message = "Missing or invalid X-Api-Key header." });
        return;
    }

    await next();
});

static bool FixedTimeEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

// ── GET /health ───────────────────────────────────────────────────────────────

app.MapGet("/health", (EmailSender email) =>
    Results.Ok(new { status = "ok", transport = email.Transport }));

// ── POST /api/email/send — raw HTML ──────────────────────────────────────────

app.MapPost("/api/email/send", async (SendEmailRequest req, EmailSender email, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(req.To))
        return Results.BadRequest(new { message = "'to' is required." });
    if (string.IsNullOrWhiteSpace(req.Subject))
        return Results.BadRequest(new { message = "'subject' is required." });
    if (string.IsNullOrWhiteSpace(req.Html))
        return Results.BadRequest(new { message = "'html' is required." });

    try
    {
        var id = await email.SendAsync(req.To, req.ToName ?? req.To, req.Subject, req.Html,
            category: req.Category);
        return Results.Ok(new { id, transport = email.Transport, status = "sent" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "send failed for {To}", req.To);
        return Results.Json(new { status = "failed", transport = email.Transport, error = ex.Message },
            statusCode: StatusCodes.Status502BadGateway);
    }
});

// ── POST /api/email/send-template — named template + {{Placeholder}} data ────

app.MapPost("/api/email/send-template", async (SendTemplateRequest req, EmailSender email, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(req.Template))
        return Results.BadRequest(new { message = "'template' is required.", available = EmailTemplateRegistry.Keys });
    if (string.IsNullOrWhiteSpace(req.To))
        return Results.BadRequest(new { message = "'to' is required." });

    var tmpl = EmailTemplateRegistry.Get(req.Template);
    if (tmpl == null)
        return Results.NotFound(new { message = $"Unknown template '{req.Template}'.", available = EmailTemplateRegistry.Keys });

    var values = TemplateRenderer.BuildValues(req.Data);

    // This service has no product DB — line items and recommendation cards come
    // from the caller and are rendered here into the branded blocks.
    if (req.LineItems is { Count: > 0 })
        values["LineItems"] = EmailHtmlBuilders.BuildLineItemsHtml(req.LineItems);

    var effectiveStorefront = values.TryGetValue("StorefrontUrl", out var sf) && !string.IsNullOrWhiteSpace(sf)
        ? sf : storefrontUrl;
    values.TryAdd("StorefrontUrl", effectiveStorefront);

    if (!values.ContainsKey("Recommendations"))
        values["Recommendations"] = req.RecommendationsHtml
            ?? EmailHtmlBuilders.BuildRecommendationsHtml(req.Recommendations, effectiveStorefront);

    var subject = TemplateRenderer.Render(tmpl.Subject, values);
    var html = TemplateRenderer.Render(tmpl.HtmlBody, values);

    try
    {
        var id = await email.SendAsync(req.To, req.ToName ?? req.To, subject, html,
            category: tmpl.Key);
        return Results.Ok(new { id, transport = email.Transport, status = "sent", template = tmpl.Key });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "send-template {Template} failed for {To}", tmpl.Key, req.To);
        return Results.Json(new { status = "failed", transport = email.Transport, error = ex.Message },
            statusCode: StatusCodes.Status502BadGateway);
    }
});

// ── POST /api/email/preview-all — every template with sample data to one inbox ─

app.MapPost("/api/email/preview-all", async (PreviewAllRequest req, EmailSender email, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(req.To))
        return Results.BadRequest(new { message = "A recipient email is required." });
    if (!email.IsConfigured)
        return Results.BadRequest(new { message = "Email is not configured (transport = none)." });

    var sample = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["RecipientName"] = "Ivi",
        ["ClientName"] = "Ivi",
        ["ContactName"] = "Ivi",
        ["CompanyName"] = "Acme Cosmetics",
        ["OrderNumber"] = "SF-20260703-SAMPLE",
        ["OrderDate"] = "3 July 2026",
        ["InvoiceNumber"] = "INV-2026-0001",
        ["RecurringOrderName"] = "Monthly Jar Restock",
        ["LineItems"] = EmailHtmlBuilders.BuildLineItemsHtml(new[]
        {
            new OrderLineItem("Adaeze Amber Dropper Bottle", "VL942839135338-5ML-0", 2500, 5.00m, 12500.00m),
        }),
        ["Recommendations"] = EmailHtmlBuilders.BuildRecommendationsHtml(new[]
        {
            new RecommendationCard("Frosted Glass Jar 50ml", 8.50m, null, $"{storefrontUrl}/shop"),
            new RecommendationCard("Matte Black Pump Bottle", 12.00m, null, $"{storefrontUrl}/shop"),
            new RecommendationCard("Kraft Shipping Box", 6.20m, null, $"{storefrontUrl}/shop"),
        }, storefrontUrl),
        ["SubtotalZAR"] = "12,500.00",
        ["ShippingZAR"] = "297.50",
        ["TotalZAR"] = "12,797.50",
        ["ShippingServiceName"] = "Air Standard",
        ["ShippingAddress"] = "Ivi<br>16 Beach Road<br>Cape Town, Western Cape 7139<br>South Africa",
        ["TrackingNumber"] = "YT1234567890ZA",
        ["TrackingCarrier"] = "YunExpress",
        ["StorefrontUrl"] = storefrontUrl,
    };

    var results = new List<object>();
    foreach (var key in EmailTemplateRegistry.Keys)
    {
        var tmpl = EmailTemplateRegistry.Get(key)!;
        var subject = "[TEST] " + TemplateRenderer.Render(tmpl.Subject, sample);
        var html = TemplateRenderer.Render(tmpl.HtmlBody, sample);
        try
        {
            await email.SendAsync(req.To, "Ivi", subject, html, category: $"preview_{key}");
            results.Add(new { key, status = "sent" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Preview email {Key} to {To} failed", key, req.To);
            results.Add(new { key, status = "failed", error = ex.Message });
        }
    }

    return Results.Ok(new { to = req.To, transport = email.Transport, results });
});

app.Run();
