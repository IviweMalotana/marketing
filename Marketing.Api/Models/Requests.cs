using System.Text.Json;

namespace Marketing.Api.Models;

/// <summary>Base64-encoded file attachment (e.g. an invoice PDF).</summary>
public record EmailAttachment(
    string Filename,
    string ContentBase64,
    string? ContentType);

public record SendEmailRequest(
    string? To,
    string? ToName,
    string? Subject,
    string? Html,
    string? Category,
    List<EmailAttachment>? Attachments);

/// <summary>One order line for the {{LineItems}} table (order_confirmation).</summary>
public record OrderLineItem(
    string ProductName,
    string Sku,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

/// <summary>One product card for the "Shop more from Be Different" block.</summary>
public record RecommendationCard(
    string Name,
    decimal? Price,
    string? ImageUrl,
    string Url);

public record SendTemplateRequest(
    string? Template,
    string? To,
    string? ToName,
    Dictionary<string, JsonElement>? Data,
    List<OrderLineItem>? LineItems,
    List<RecommendationCard>? Recommendations,
    string? RecommendationsHtml,
    List<EmailAttachment>? Attachments);

public record PreviewAllRequest(string? To);
