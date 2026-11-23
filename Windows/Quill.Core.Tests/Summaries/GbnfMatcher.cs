using System.Globalization;
using System.Text;

namespace Quill.Core.Tests.Summaries;

/// <summary>
/// Minimal GBNF recognizer for tests: rules "name ::= body" (one per line), quoted literals,
/// character classes (ranges, negation, \xNN escapes), rule references, groups, alternation
/// and the postfix operators ?, * and +. Enough to prove that the grammar we hand to
/// llama.cpp accepts the JSON we expect and rejects what it must reject.
/// </summary>
internal sealed class GbnfMatcher
{
    private abstract record Node;

    private sealed record Literal(string Text) : Node;

    private sealed record CharClass(bool Negated, List<(char From, char To)> Ranges) : Node;

    private sealed record RuleRef(string Name) : Node;

    private sealed record Sequence(List<Node> Items) : Node;

    private sealed record Alternatives(List<Node> Options) : Node;

    private sealed record Repeat(Node Item, int Min, bool Unbounded) : Node;

    private readonly Dictionary<string, Node> _rules = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, int), HashSet<int>> _memo = [];
    private string _input = string.Empty;

    public GbnfMatcher(string grammar)
    {
        foreach (var raw in grammar.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var split = line.IndexOf("::=", StringComparison.Ordinal);
            if (split < 0)
            {
                throw new FormatException($"not a rule: {line}");
            }

            var name = line[..split].Trim();
            var position = 0;
            var body = line[(split + 3)..];
            _rules[name] = ParseAlternatives(body, ref position);
            SkipSpaces(body, ref position);
            if (position != body.Length)
            {
                throw new FormatException($"trailing input in rule {name}: {body[position..]}");
            }
        }

        foreach (var reference in _rules.Values.SelectMany(References))
        {
            if (!_rules.ContainsKey(reference))
            {
                throw new FormatException($"undefined rule {reference}");
            }
        }
    }

    public IReadOnlyCollection<string> RuleNames => _rules.Keys;

    public bool Matches(string input, string root = "root")
    {
        _input = input;
        _memo.Clear();
        return MatchRule(root, 0).Contains(input.Length);
    }

    private IEnumerable<string> References(Node node) => node switch
    {
        RuleRef r => [r.Name],
        Sequence s => s.Items.SelectMany(References),
        Alternatives a => a.Options.SelectMany(References),
        Repeat r => References(r.Item),
        _ => [],
    };

    private HashSet<int> MatchRule(string name, int position)
    {
        if (_memo.TryGetValue((name, position), out var cached))
        {
            return cached;
        }

        _memo[(name, position)] = [];
        var result = Match(_rules[name], position);
        _memo[(name, position)] = result;
        return result;
    }

    private HashSet<int> Match(Node node, int position)
    {
        switch (node)
        {
            case Literal literal:
                return string.CompareOrdinal(_input, position, literal.Text, 0, literal.Text.Length) == 0 &&
                    position + literal.Text.Length <= _input.Length
                    ? [position + literal.Text.Length]
                    : [];
            case CharClass charClass:
                if (position >= _input.Length)
                {
                    return [];
                }

                var c = _input[position];
                var inside = charClass.Ranges.Any(range => c >= range.From && c <= range.To);
                return inside != charClass.Negated ? [position + 1] : [];
            case RuleRef reference:
                return MatchRule(reference.Name, position);
            case Sequence sequence:
                HashSet<int> current = [position];
                foreach (var item in sequence.Items)
                {
                    current = current.SelectMany(p => Match(item, p)).ToHashSet();
                    if (current.Count == 0)
                    {
                        break;
                    }
                }

                return current;
            case Alternatives alternatives:
                return alternatives.Options.SelectMany(option => Match(option, position)).ToHashSet();
            case Repeat repeat:
                HashSet<int> reached = repeat.Min == 0 ? [position] : [];
                HashSet<int> frontier = [position];
                for (var count = 1; frontier.Count > 0; count++)
                {
                    frontier = frontier.SelectMany(p => Match(repeat.Item, p)).Where(p => !reached.Contains(p) || count <= repeat.Min).ToHashSet();
                    if (count >= repeat.Min)
                    {
                        reached.UnionWith(frontier);
                    }

                    if (!repeat.Unbounded && count >= 1)
                    {
                        break;
                    }
                }

                return reached;
            default:
                throw new InvalidOperationException(node.GetType().Name);
        }
    }

    private static Node ParseAlternatives(string text, ref int position)
    {
        var options = new List<Node> { ParseSequence(text, ref position) };
        SkipSpaces(text, ref position);
        while (position < text.Length && text[position] == '|')
        {
            position++;
            options.Add(ParseSequence(text, ref position));
            SkipSpaces(text, ref position);
        }

        return options.Count == 1 ? options[0] : new Alternatives(options);
    }

    private static Node ParseSequence(string text, ref int position)
    {
        var items = new List<Node>();
        while (true)
        {
            SkipSpaces(text, ref position);
            if (position >= text.Length || text[position] is '|' or ')')
            {
                return new Sequence(items);
            }

            Node item;
            var c = text[position];
            if (c == '"')
            {
                position++;
                var literal = new StringBuilder();
                while (text[position] != '"')
                {
                    literal.Append(ReadChar(text, ref position));
                }

                position++;
                item = new Literal(literal.ToString());
            }
            else if (c == '[')
            {
                position++;
                var negated = text[position] == '^';
                if (negated)
                {
                    position++;
                }

                var ranges = new List<(char, char)>();
                while (text[position] != ']')
                {
                    var from = ReadChar(text, ref position);
                    var to = from;
                    if (text[position] == '-' && text[position + 1] != ']')
                    {
                        position++;
                        to = ReadChar(text, ref position);
                    }

                    ranges.Add((from, to));
                }

                position++;
                item = new CharClass(negated, ranges);
            }
            else if (c == '(')
            {
                position++;
                item = ParseAlternatives(text, ref position);
                if (text[position] != ')')
                {
                    throw new FormatException("unclosed group");
                }

                position++;
            }
            else if (char.IsLetterOrDigit(c) || c == '-')
            {
                var start = position;
                while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '-'))
                {
                    position++;
                }

                item = new RuleRef(text[start..position]);
            }
            else
            {
                throw new FormatException($"unexpected '{c}' at {position} in: {text}");
            }

            if (position < text.Length && text[position] is '?' or '*' or '+')
            {
                item = text[position] switch
                {
                    '?' => new Repeat(item, 0, false),
                    '*' => new Repeat(item, 0, true),
                    _ => new Repeat(item, 1, true),
                };
                position++;
            }

            items.Add(item);
        }
    }

    private static char ReadChar(string text, ref int position)
    {
        var c = text[position++];
        if (c != '\\')
        {
            return c;
        }

        var escaped = text[position++];
        switch (escaped)
        {
            case 'n': return '\n';
            case 't': return '\t';
            case 'r': return '\r';
            case 'x':
                var hex = text.Substring(position, 2);
                position += 2;
                return (char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            default: return escaped;
        }
    }

    private static void SkipSpaces(string text, ref int position)
    {
        while (position < text.Length && text[position] == ' ')
        {
            position++;
        }
    }
}
