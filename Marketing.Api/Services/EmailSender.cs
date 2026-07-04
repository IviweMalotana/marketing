using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Marketing.Api.Services;

/// <summary>
/// Email transport ported from BDP.API's EmailService. Prefers Resend's HTTPS API
/// (port 443) because Railway and most cloud hosts block outbound SMTP ports;
/// SMTP/STARTTLS remains as a local-dev fallback. No database dependency —
/// delivery outcomes go to the structured logger only.
/// </summary>
public class EmailSender
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailSender> _logger;
    private readonly IHttpClientFactory _httpFactory;

    public EmailSender(IConfiguration config, ILogger<EmailSender> logger, IHttpClientFactory httpFactory)
    {
        _config = config;
        _logger = logger;
        _httpFactory = httpFactory;
    }

    /// <summary>
    /// Resolves the Resend API key. Prefers an explicit Resend__ApiKey; otherwise,
    /// when Email__SmtpHost points at Resend's SMTP relay, the SMTP password *is*
    /// the API key — so an existing SMTP-style config keeps working over HTTPS with
    /// no extra env vars. Returns null when no Resend key is available.
    /// </summary>
    private string? ResendApiKey
    {
        get
        {
            var explicitKey = _config["Resend:ApiKey"] ?? _config["Resend__ApiKey"];
            if (!string.IsNullOrEmpty(explicitKey)) return explicitKey;

            var host = _config["Email:SmtpHost"] ?? _config["Email__SmtpHost"];
            if (!string.IsNullOrEmpty(host) && host.Contains("resend", StringComparison.OrdinalIgnoreCase))
                return _config["Email:SmtpPassword"] ?? _config["Email__SmtpPassword"];

            return null;
        }
    }

    private string? SmtpHost => _config["Email:SmtpHost"] ?? _config["Email__SmtpHost"];

    public bool IsConfigured =>
        !string.IsNullOrEmpty(ResendApiKey) || !string.IsNullOrEmpty(SmtpHost);

    /// <summary>
    /// Which transport the running app will actually use for the next send:
    /// "resend" (HTTPS API, preferred), "smtp" (fallback — blocked on most cloud
    /// hosts), or "none". Surfaced on /health so callers can see at a glance
    /// whether the Resend key is being picked up at runtime.
    /// </summary>
    public string Transport =>
        !string.IsNullOrEmpty(ResendApiKey) ? "resend"
        : !string.IsNullOrEmpty(SmtpHost) ? "smtp"
        : "none";

    public string FromName => _config["Email:FromName"] ?? "BDP";

    public string FromAddress => _config["Email:FromAddress"] ?? "noreply@bedifferentpackaging.com";

    /// <summary>
    /// Sends one email over the active transport. Returns the provider message id
    /// (Resend only; null over SMTP). Throws when no transport is configured or the
    /// provider rejects the send — callers translate that into a 502.
    /// </summary>
    public async Task<string?> SendAsync(string toEmail, string toName, string subject, string htmlBody,
        IReadOnlyList<(byte[] data, string fileName, string contentType)>? attachments = null,
        string? category = null)
    {
        var resendKey = ResendApiKey;
        var host = SmtpHost;

        if (string.IsNullOrEmpty(resendKey) && string.IsNullOrEmpty(host))
        {
            _logger.LogWarning("Email not configured — cannot send to {Email} ({Category})", toEmail, category);
            throw new InvalidOperationException(
                "Email transport not configured. Set Resend__ApiKey (production) or Email__SmtpHost (local dev).");
        }

        try
        {
            string? id;
            if (!string.IsNullOrEmpty(resendKey))
                id = await SendViaResendAsync(resendKey, toEmail, subject, htmlBody, attachments);
            else
            {
                await SendViaSmtpAsync(host!, toEmail, toName, subject, htmlBody, attachments);
                id = null;
            }

            _logger.LogInformation("Email sent to {Email} ({Category}) via {Transport}: {Subject}",
                toEmail, category, Transport, subject);
            return id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email send to {Email} ({Category}) failed: {Subject}", toEmail, category, subject);
            throw;
        }
    }

    /// <summary>Sends via the Resend REST API over HTTPS (not blocked by cloud SMTP firewalls).</summary>
    private async Task<string?> SendViaResendAsync(string apiKey, string toEmail, string subject,
        string htmlBody, IReadOnlyList<(byte[] data, string fileName, string contentType)>? attachments)
    {
        var payload = new Dictionary<string, object?>
        {
            ["from"] = $"{FromName} <{FromAddress}>",
            ["to"] = new[] { toEmail },
            ["subject"] = subject,
            ["html"] = htmlBody,
        };

        if (attachments is { Count: > 0 })
        {
            payload["attachments"] = attachments.Select(a => new Dictionary<string, object?>
            {
                ["filename"] = a.fileName,
                ["content"] = Convert.ToBase64String(a.data),
            }).ToArray();
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var client = _httpFactory.CreateClient();
        using var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Resend API returned {(int)resp.StatusCode}: {body}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null; // sent fine; id just wasn't parseable
        }
    }

    /// <summary>Sends via SMTP/STARTTLS (MailKit). Used for local dev or any non-Resend host.</summary>
    private async Task SendViaSmtpAsync(string host, string toEmail, string toName, string subject,
        string htmlBody, IReadOnlyList<(byte[] data, string fileName, string contentType)>? attachments)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(FromName, FromAddress));
        message.To.Add(new MailboxAddress(toName, toEmail));
        message.Subject = subject;

        var builder = new BodyBuilder { HtmlBody = htmlBody };
        foreach (var a in attachments ?? Array.Empty<(byte[], string, string)>())
        {
            builder.Attachments.Add(a.fileName, a.data, ContentType.Parse(a.contentType));
        }
        message.Body = builder.ToMessageBody();

        using var smtp = new SmtpClient();
        // Fail fast instead of hanging ~100s when a cloud host blocks SMTP ports.
        smtp.Timeout = 15000; // ms
        await smtp.ConnectAsync(host,
            int.Parse(_config["Email:SmtpPort"] ?? "587"),
            SecureSocketOptions.StartTls);
        await smtp.AuthenticateAsync(
            _config["Email:SmtpUser"] ?? string.Empty,
            _config["Email:SmtpPassword"] ?? string.Empty);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
    }
}
