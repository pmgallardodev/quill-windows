using System.Text.Json;
using System.Text.Json.Nodes;
using Quill.Core.Api;

namespace Quill.Core.Settings;

/// <summary>
/// Reads and writes <see cref="ApiSettings"/> (the "api" section of config.json) with the same
/// rules as <see cref="SettingsDocument"/>: lenient key-by-key parsing, and a merge that only
/// touches the keys it owns and refuses to overwrite a file that is not a JSON object.
/// Invalid webhook URLs are dropped (never sent to), an invalid token or port reads as missing.
/// </summary>
public static class ApiSettingsDocument
{
    private const string SectionName = "api";
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static ApiSettings Parse(string? json)
    {
        JsonObject? section;
        try
        {
            section = (string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject)?[SectionName] as JsonObject;
        }
        catch (JsonException)
        {
            section = null;
        }

        if (section is null)
        {
            return new ApiSettings();
        }

        var token = section["token"] is JsonValue tokenValue && tokenValue.TryGetValue<string>(out var text) ? text.Trim() : null;
        var port = section["port"] is JsonValue portValue && portValue.GetValueKind() == JsonValueKind.Number
            && portValue.TryGetValue<int>(out var number) && ApiSettings.IsValidPort(number)
                ? number
                : 0;

        return new ApiSettings
        {
            Enabled = Boolean(section, "enabled") ?? false,
            Port = port,
            Token = ApiToken.IsWellFormed(token) ? token : null,
            Webhooks = Webhooks(section["webhooks"] as JsonArray),
            McpEnabled = Boolean(section, "mcp_enabled") ?? false,
        };
    }

    /// <summary>Returns <paramref name="existingJson"/> with the "api" keys replaced; unknown keys survive.</summary>
    /// <exception cref="FormatException">The existing file is not a JSON object.</exception>
    public static string Merge(string? existingJson, ApiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        JsonObject root;
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            root = [];
        }
        else
        {
            try
            {
                root = JsonNode.Parse(existingJson) as JsonObject
                    ?? throw new FormatException("config.json is not a JSON object");
            }
            catch (JsonException exception)
            {
                throw new FormatException("config.json is not valid JSON", exception);
            }
        }

        if (root[SectionName] is not JsonObject section)
        {
            section = [];
            root[SectionName] = section;
        }

        section["enabled"] = settings.Enabled;
        SetOrRemove(section, "port", ApiSettings.IsValidPort(settings.Port) ? JsonValue.Create(settings.Port) : null);
        SetOrRemove(section, "token", ApiToken.IsWellFormed(settings.Token) ? JsonValue.Create(settings.Token) : null);
        section["webhooks"] = new JsonArray(Valid(settings.Webhooks).Select(url => (JsonNode?)JsonValue.Create(url)).ToArray());
        section["mcp_enabled"] = settings.McpEnabled;
        return root.ToJsonString(WriteOptions);
    }

    private static IReadOnlyList<string> Webhooks(JsonArray? array) =>
        array is null
            ? []
            : Valid(array.Select(node => node is JsonValue value && value.TryGetValue<string>(out var url) ? url : null));

    private static List<string> Valid(IEnumerable<string?> urls) =>
        urls.Where(url => LoopbackUrl.TryParse(url, out _, out _))
            .Select(url => url!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(ApiSettings.MaxWebhooks)
            .ToList();

    private static void SetOrRemove(JsonObject section, string key, JsonNode? value)
    {
        if (value is null)
        {
            section.Remove(key);
        }
        else
        {
            section[key] = value;
        }
    }

    private static bool? Boolean(JsonObject section, string key) =>
        section[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}
