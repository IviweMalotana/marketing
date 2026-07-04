using System.Globalization;
using Marketing.Api.Models;

namespace Marketing.Api.Services;

/// <summary>
/// HTML fragment builders ported from BDP.API's OrderEmailService. This service
/// has no product database, so callers pass line items and recommendation cards
/// in the request payload and these builders render them into the template's
/// {{LineItems}} and {{Recommendations}} placeholders.
/// </summary>
public static class EmailHtmlBuilders
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Esc(string? s) => System.Web.HttpUtility.HtmlEncode(s ?? string.Empty);

    /// <summary>
    /// Renders order line items as a clean two-column table (name + qty/SKU on the
    /// left, line total + unit price on the right).
    /// </summary>
    public static string BuildLineItemsHtml(IEnumerable<OrderLineItem> lines)
    {
        var rows = string.Concat(lines.Select(line =>
            "<tr>" +
              "<td style=\"padding:12px 0;border-bottom:1px solid #E8E0D8;vertical-align:top;\">" +
                $"<p style=\"margin:0;font-size:14px;color:#1C1A17;\">{Esc(line.ProductName)}</p>" +
                $"<p style=\"margin:3px 0 0;font-size:12px;color:#9E8F83;\">Qty {line.Quantity} &nbsp;&middot;&nbsp; SKU {Esc(line.Sku)}</p>" +
              "</td>" +
              "<td align=\"right\" style=\"padding:12px 0;border-bottom:1px solid #E8E0D8;vertical-align:top;white-space:nowrap;\">" +
                $"<p style=\"margin:0;font-size:14px;color:#1C1A17;\">R {line.LineTotal.ToString("N2", Inv)}</p>" +
                $"<p style=\"margin:3px 0 0;font-size:12px;color:#9E8F83;\">R {line.UnitPrice.ToString("N2", Inv)} each</p>" +
              "</td>" +
            "</tr>"));

        return "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:0 0 8px;\">" +
               rows + "</table>";
    }

    /// <summary>
    /// Builds the "Shop more from Be Different" upsell block — product cards
    /// (image, name, from-price) plus a "Shop the full range" CTA to the storefront
    /// shop page. Returns "" when there are no cards so the surrounding template
    /// collapses cleanly.
    /// </summary>
    public static string BuildRecommendationsHtml(IReadOnlyList<RecommendationCard>? cards, string storefrontUrl)
    {
        if (cards == null || cards.Count == 0) return "";

        var cells = string.Concat(cards.Select(c =>
        {
            var imgUrl = c.ImageUrl ?? "";
            if (!string.IsNullOrEmpty(imgUrl) && !imgUrl.StartsWith("http"))
                imgUrl = $"{storefrontUrl}/{imgUrl.TrimStart('/')}";

            var priceLine = c.Price.HasValue
                ? $"<p style=\"margin:2px 0 0;font-size:12px;color:#9E8F83;\">from R {c.Price.Value.ToString("N2", Inv)}</p>"
                : "";

            var imgTag = string.IsNullOrEmpty(imgUrl)
                ? "<div style=\"width:100%;height:120px;background:#F5EFE6;border:1px solid #E8E0D8;border-radius:2px;\"></div>"
                : $"<img src=\"{Esc(imgUrl)}\" width=\"170\" alt=\"{Esc(c.Name)}\" style=\"display:block;width:100%;max-width:170px;height:auto;border:1px solid #E8E0D8;border-radius:2px;\" />";

            return $"<td width=\"33%\" valign=\"top\" style=\"padding:0 6px;\">" +
                   $"<a href=\"{Esc(c.Url)}\" style=\"text-decoration:none;color:inherit;\">" +
                   imgTag +
                   $"<p style=\"margin:8px 0 0;font-size:13px;color:#1C1A17;line-height:1.3;\">{Esc(c.Name)}</p>" +
                   priceLine +
                   "</a></td>";
        }));

        return
            "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\">" +
              "<tr><td style=\"padding:24px 0;\"><div style=\"height:1px;background:#C9B8A8;\"></div></td></tr>" +
            "</table>" +
            "<p style=\"margin:0 0 4px;font-size:10px;text-transform:uppercase;letter-spacing:0.2em;color:#9E8F83;\">You might also like</p>" +
            "<p style=\"margin:0 0 16px;font-size:18px;color:#1C1A17;\">Shop more from Be Different</p>" +
            "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>" + cells + "</tr></table>" +
            "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin-top:18px;\">" +
              "<tr><td align=\"center\">" +
                $"<a href=\"{Esc(storefrontUrl)}/shop\" style=\"display:inline-block;padding:11px 26px;border:1px solid #1C1A17;color:#1C1A17;font-family:Georgia,'Times New Roman',serif;font-size:13px;letter-spacing:0.1em;text-decoration:none;border-radius:2px;\">Shop the full range</a>" +
              "</td></tr>" +
            "</table>";
    }
}
