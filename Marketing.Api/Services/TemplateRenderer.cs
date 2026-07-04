using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Marketing.Api.Services;

/// <summary>
/// {{Placeholder}} substitution. Values are injected as raw HTML (callers pass
/// pre-escaped values, matching the original BDP.API behaviour); placeholders
/// with no matching value render as "" so optional blocks collapse cleanly.
/// </summary>
public static class TemplateRenderer
{
    private static readonly Regex Placeholder = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    public static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder.Replace(template, m =>
            values.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

    /// <summary>
    /// Flattens the caller's JSON data payload into placeholder values. Keys are
    /// matched case-insensitively, so camelCase JSON ("orderNumber") fills the
    /// templates' PascalCase tokens ({{OrderNumber}}). Numeric values for keys
    /// ending in "ZAR" are formatted as N2 ("12,797.50") so amounts render the
    /// same way BDP.API rendered them.
    /// </summary>
    public static Dictionary<string, string> BuildValues(Dictionary<string, JsonElement>? data)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data == null) return values;

        foreach (var (key, el) in data)
        {
            values[key] = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString() ?? "",
                JsonValueKind.Number when key.EndsWith("ZAR", StringComparison.OrdinalIgnoreCase)
                    => el.GetDecimal().ToString("N2", CultureInfo.InvariantCulture),
                JsonValueKind.Number => el.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                _ => el.GetRawText(),
            };
        }
        return values;
    }
}
