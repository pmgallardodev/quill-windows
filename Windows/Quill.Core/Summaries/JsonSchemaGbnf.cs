using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Quill.Core.Summaries;

/// <summary>
/// Converts the JSON Schema subset used by <see cref="MeetingSummarySchema"/> into a llama.cpp
/// GBNF grammar (root rule "root"). LLamaSharp has no schema-to-grammar converter, so this is
/// what constrains the model's JSON output.
/// <para>
/// Supported: <c>type</c> object (every property required, <c>additionalProperties: false</c>,
/// properties emitted in schema order), array (<c>items</c>), string (<c>minLength</c>,
/// <c>pattern</c> made of anchored literals and character classes with quantifiers), boolean,
/// null, and type unions such as <c>["string","null"]</c>. <c>description</c> and <c>title</c>
/// are ignored. Anything else throws <see cref="NotSupportedException"/> instead of producing a
/// grammar that silently allows invalid output.
/// </para>
/// Whitespace between tokens is bounded (<see cref="MaxWhitespace"/> characters) so a model
/// cannot loop on spaces.
/// </summary>
public static class JsonSchemaGbnf
{
    public const int MaxWhitespace = 20;

    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal) { "description", "title" };

    public static string Convert(string jsonSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonSchema);
        using var document = JsonDocument.Parse(jsonSchema);
        var builder = new Builder();
        builder.Rules["root"] = builder.Value(document.RootElement, "root");
        return builder.Render();
    }

    private sealed class Builder
    {
        public Dictionary<string, string> Rules { get; } = new(StringComparer.Ordinal);

        public string Render()
        {
            Rules["ws"] = BoundedWhitespace();
            Rules["char"] = """[^"\\\x7F\x00-\x1F] | "\\" (["\\/bfnrt] | "u" hex hex hex hex)""";
            Rules["hex"] = "[0-9a-fA-F]";

            var output = new StringBuilder();
            output.Append("root ::= ").Append(Rules["root"]).Append('\n');
            foreach (var (name, body) in Rules.Where(rule => rule.Key != "root"))
            {
                output.Append(name).Append(" ::= ").Append(body).Append('\n');
            }

            return output.ToString();
        }

        /// <summary>Returns a GBNF expression for <paramref name="schema"/>, adding named rules as needed.</summary>
        public string Value(JsonElement schema, string name)
        {
            if (schema.ValueKind != JsonValueKind.Object)
            {
                throw new NotSupportedException($"schema at '{name}' must be an object");
            }

            if (!schema.TryGetProperty("type", out var type))
            {
                throw new NotSupportedException($"schema at '{name}' has no 'type'");
            }

            if (type.ValueKind == JsonValueKind.Array)
            {
                var options = type.EnumerateArray()
                    .Select(option => Typed(schema, option.GetString() ?? string.Empty, name, union: true))
                    .ToList();
                return options.Count == 0
                    ? throw new NotSupportedException($"empty type union at '{name}'")
                    : "(" + string.Join(" | ", options) + ")";
            }

            return Typed(schema, type.GetString() ?? string.Empty, name, union: false);
        }

        private string Typed(JsonElement schema, string type, string name, bool union)
        {
            return type switch
            {
                "object" => Object(schema, name),
                "array" => Array(schema, name),
                "string" => String(schema, name),
                "boolean" => Leaf(schema, name, union, "(\"true\" | \"false\")"),
                "null" => Leaf(schema, name, union, "\"null\""),
                _ => throw new NotSupportedException($"type '{type}' at '{name}'"),
            };
        }

        private static string Leaf(JsonElement schema, string name, bool union, string grammar)
        {
            // In a union, the sibling type's keywords (e.g. minLength) belong to that type.
            if (!union)
            {
                RejectUnknownKeywords(schema, name, "type");
            }

            return grammar;
        }

        private string Object(JsonElement schema, string name)
        {
            RejectUnknownKeywords(schema, name, "type", "properties", "required", "additionalProperties");
            if (!schema.TryGetProperty("additionalProperties", out var additional) ||
                additional.ValueKind != JsonValueKind.False)
            {
                throw new NotSupportedException($"object at '{name}' must set additionalProperties to false");
            }

            var properties = schema.TryGetProperty("properties", out var props)
                ? props.EnumerateObject().ToList()
                : [];
            var required = schema.TryGetProperty("required", out var req)
                ? req.EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal)
                : [];
            if (properties.Count != required.Count || properties.Any(property => !required.Contains(property.Name)))
            {
                throw new NotSupportedException($"object at '{name}': every property must be required, and only those");
            }

            if (properties.Count == 0)
            {
                return AddRule(name, "\"{\" ws \"}\"");
            }

            var fields = properties.Select(property =>
                Literal("\"" + property.Name + "\"") + " ws \":\" ws " +
                Value(property.Value, name + "-" + RuleName(property.Name)));
            return AddRule(name, "\"{\" ws " + string.Join(" ws \",\" ws ", fields) + " ws \"}\"");
        }

        private string Array(JsonElement schema, string name)
        {
            RejectUnknownKeywords(schema, name, "type", "items");
            if (!schema.TryGetProperty("items", out var items))
            {
                throw new NotSupportedException($"array at '{name}' has no 'items'");
            }

            var itemName = name + "-item";
            var value = Value(items, itemName);
            var item = Rules.ContainsKey(value) ? value : AddRule(itemName, value);
            return AddRule(name, $"\"[\" ws ({item} (ws \",\" ws {item})*)? ws \"]\"");
        }

        private string String(JsonElement schema, string name)
        {
            RejectUnknownKeywords(schema, name, "type", "minLength", "pattern");
            if (schema.TryGetProperty("pattern", out var pattern))
            {
                if (schema.TryGetProperty("minLength", out _))
                {
                    throw new NotSupportedException($"string at '{name}' combines pattern and minLength");
                }

                return AddRule(name, "\"\\\"\" " + Pattern(pattern.GetString() ?? string.Empty, name) + " \"\\\"\"");
            }

            var minLength = schema.TryGetProperty("minLength", out var min) ? min.GetInt32() : 0;
            if (minLength is < 0 or > 64)
            {
                throw new NotSupportedException($"minLength {minLength} at '{name}'");
            }

            var body = string.Concat(Enumerable.Repeat("char ", minLength)) + "char*";
            return AddRule(minLength == 0 ? "string" : $"string-min{minLength}", "\"\\\"\" " + body + " \"\\\"\"");
        }

        private string AddRule(string name, string body)
        {
            if (name == "root")
            {
                return body;
            }

            if (Rules.TryGetValue(name, out var existing) && existing != body)
            {
                throw new InvalidOperationException($"rule '{name}' defined twice with different bodies");
            }

            Rules[name] = body;
            return name;
        }

        private static string BoundedWhitespace()
        {
            var nested = string.Empty;
            for (var i = 0; i < MaxWhitespace; i++)
            {
                nested = i == 0 ? "([ \\t\\n])?" : "([ \\t\\n] " + nested + ")?";
            }

            return nested;
        }

        private static void RejectUnknownKeywords(JsonElement schema, string name, params string[] allowed)
        {
            foreach (var property in schema.EnumerateObject())
            {
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) && !Annotations.Contains(property.Name))
                {
                    throw new NotSupportedException($"keyword '{property.Name}' at '{name}'");
                }
            }
        }
    }

    /// <summary>
    /// Translates an anchored regex made of literal characters, escapes and character classes,
    /// each optionally followed by ?, *, +, {n}, {n,} or {n,m}. Groups and alternation throw.
    /// </summary>
    private static string Pattern(string pattern, string name)
    {
        if (!pattern.StartsWith('^') || !pattern.EndsWith('$') || pattern.EndsWith("\\$", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"pattern at '{name}' must be anchored with ^ and $");
        }

        var body = pattern[1..^1];
        var parts = new List<string>();
        var i = 0;
        while (i < body.Length)
        {
            string atom;
            var c = body[i];
            if (c == '[')
            {
                var end = body.IndexOf(']', i + 1);
                if (end < 0)
                {
                    throw new NotSupportedException($"unclosed class in pattern at '{name}'");
                }

                var content = body[(i + 1)..end];
                if (content.Contains('\\', StringComparison.Ordinal) || content.Contains('[', StringComparison.Ordinal))
                {
                    throw new NotSupportedException($"escapes inside classes in pattern at '{name}'");
                }

                atom = "[" + content + "]";
                i = end + 1;
            }
            else if (c == '\\' && i + 1 < body.Length && !char.IsLetterOrDigit(body[i + 1]))
            {
                atom = Literal(body[i + 1].ToString());
                i += 2;
            }
            else if ("()|.\\^$".Contains(c, StringComparison.Ordinal))
            {
                throw new NotSupportedException($"'{c}' in pattern at '{name}'");
            }
            else
            {
                atom = Literal(c.ToString());
                i++;
            }

            parts.Add(Quantified(atom, body, ref i, name));
        }

        return parts.Count == 0 ? "\"\"" : string.Join(" ", parts);
    }

    private static string Quantified(string atom, string body, ref int i, string name)
    {
        if (i >= body.Length)
        {
            return atom;
        }

        switch (body[i])
        {
            case '?' or '*' or '+':
                return atom + body[i++];
            case '{':
                var end = body.IndexOf('}', i);
                if (end < 0)
                {
                    throw new NotSupportedException($"unclosed quantifier in pattern at '{name}'");
                }

                var bounds = body[(i + 1)..end].Split(',');
                i = end + 1;
                var min = int.Parse(bounds[0], CultureInfo.InvariantCulture);
                var repeated = string.Join(" ", Enumerable.Repeat(atom, min));
                if (bounds.Length == 1)
                {
                    return repeated;
                }

                if (bounds[1].Length == 0)
                {
                    return (repeated + " " + atom + "*").Trim();
                }

                var optional = int.Parse(bounds[1], CultureInfo.InvariantCulture) - min;
                return (repeated + " " + string.Join(" ", Enumerable.Repeat(atom + "?", optional))).Trim();
            default:
                return atom;
        }
    }

    /// <summary>A GBNF double-quoted literal; quotes, backslashes and control characters escaped.</summary>
    private static string Literal(string text)
    {
        var output = new StringBuilder("\"");
        foreach (var c in text)
        {
            output.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                < ' ' or (char)0x7F => "\\x" + ((int)c).ToString("X2", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        }

        return output.Append('"').ToString();
    }

    private static string RuleName(string property)
    {
        var output = new StringBuilder();
        foreach (var c in property)
        {
            if (char.IsUpper(c))
            {
                output.Append('-').Append(char.ToLowerInvariant(c));
            }
            else if (char.IsAsciiLetterOrDigit(c))
            {
                output.Append(c);
            }
            else
            {
                output.Append('-');
            }
        }

        return output.ToString();
    }
}
