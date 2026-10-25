using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// A compiled JSONPath expression (Goessner / RFC 9535 subset) evaluated over <see cref="JsonNode"/>.
/// Supports: $ @ .name ['name'] [n] [-n] [*] .* [a,b] [start:end:step] ..name ..* and filters
/// [?(@.a == 'x' &amp;&amp; @.b &gt; 1 || !@.c)] with == != &lt; &lt;= &gt; &gt;= =~ /regex/flags.
/// </summary>
public sealed class JsonPath
{
    private static readonly ConcurrentDictionary<string, JsonPath> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Segment[] _segments;

    private JsonPath(string expression, bool relative, Segment[] segments)
    {
        Expression = expression;
        IsRelative = relative;
        _segments = segments;
    }

    /// <summary>Gets the source expression.</summary>
    public string Expression { get; }

    /// <summary>Gets a value indicating whether the path starts with @.</summary>
    public bool IsRelative { get; }

    /// <summary>Parses (with caching) an expression.</summary>
    /// <param name="expression">The expression.</param>
    /// <returns>The compiled path.</returns>
    /// <exception cref="FormatException">Invalid expression.</exception>
    public static JsonPath Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (Cache.TryGetValue(expression, out var cached))
        {
            return cached;
        }

        var parser = new Parser(expression);
        var compiled = parser.ParsePath();
        if (Cache.Count < 4096)
        {
            Cache[expression] = compiled;
        }

        return compiled;
    }

    /// <summary>Tries to parse an expression.</summary>
    /// <param name="expression">The expression.</param>
    /// <param name="path">The compiled path.</param>
    /// <param name="error">The error message.</param>
    /// <returns>Whether parsing succeeded.</returns>
    public static bool TryParse(string expression, out JsonPath? path, out string? error)
    {
        try
        {
            path = Parse(expression);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            path = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Selects all matching nodes.</summary>
    /// <param name="root">The document root ($).</param>
    /// <param name="current">The current node (@), defaults to root.</param>
    /// <returns>The matches (may contain null for JSON null).</returns>
    public IReadOnlyList<JsonNode?> Select(JsonNode? root, JsonNode? current = null)
    {
        IList<JsonNode?> nodes = new List<JsonNode?> { IsRelative ? (current ?? root) : root };
        foreach (var segment in _segments)
        {
            var next = new List<JsonNode?>();
            foreach (var node in nodes)
            {
                segment.Apply(node, root, next);
            }

            nodes = next;
            if (nodes.Count == 0)
            {
                break;
            }
        }

        return (IReadOnlyList<JsonNode?>)nodes;
    }

    /// <summary>Selects the first match.</summary>
    /// <param name="root">The root.</param>
    /// <param name="current">The current node.</param>
    /// <returns>The first match or null.</returns>
    public JsonNode? SelectFirst(JsonNode? root, JsonNode? current = null)
    {
        var all = Select(root, current);
        return all.Count > 0 ? all[0] : null;
    }

    /// <inheritdoc />
    public override string ToString() => Expression;

    /// <summary>Converts a node to its scalar string form (strings unquoted, numbers invariant).</summary>
    /// <param name="node">The node.</param>
    /// <returns>The text or null.</returns>
    public static string? ToText(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s))
            {
                return s;
            }

            if (v.TryGetValue<bool>(out var b))
            {
                return b ? "true" : "false";
            }

            if (v.TryGetValue<JsonElement>(out var el))
            {
                return el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString(),
                    JsonValueKind.Number => el.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => null,
                    _ => el.GetRawText(),
                };
            }

            if (v.TryGetValue<double>(out var d))
            {
                return d.ToString(CultureInfo.InvariantCulture);
            }

            if (v.TryGetValue<long>(out var l))
            {
                return l.ToString(CultureInfo.InvariantCulture);
            }

            return v.ToJsonString();
        }

        return node.ToJsonString();
    }

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out value))
            {
                return true;
            }

            if (v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number)
            {
                value = el.GetDouble();
                return true;
            }
        }

        return false;
    }

    private abstract class Segment
    {
        public abstract void Apply(JsonNode? node, JsonNode? root, List<JsonNode?> output);
    }

    private sealed class ChildSegment : Segment
    {
        private readonly Selector[] _selectors;

        public ChildSegment(Selector[] selectors) => _selectors = selectors;

        public override void Apply(JsonNode? node, JsonNode? root, List<JsonNode?> output)
        {
            foreach (var s in _selectors)
            {
                s.Select(node, root, output);
            }
        }
    }

    private sealed class DescendantSegment : Segment
    {
        private readonly Selector[] _selectors;

        public DescendantSegment(Selector[] selectors) => _selectors = selectors;

        public override void Apply(JsonNode? node, JsonNode? root, List<JsonNode?> output)
        {
            var stack = new Stack<JsonNode?>();
            stack.Push(node);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 200_000)
            {
                var current = stack.Pop();
                foreach (var s in _selectors)
                {
                    s.Select(current, root, output);
                }

                if (current is JsonObject o)
                {
                    var children = new List<JsonNode?>();
                    foreach (var kv in o)
                    {
                        children.Add(kv.Value);
                    }

                    for (var i = children.Count - 1; i >= 0; i--)
                    {
                        stack.Push(children[i]);
                    }
                }
                else if (current is JsonArray a)
                {
                    for (var i = a.Count - 1; i >= 0; i--)
                    {
                        stack.Push(a[i]);
                    }
                }
            }
        }
    }

    private abstract class Selector
    {
        public abstract void Select(JsonNode? node, JsonNode? root, List<JsonNode?> output);
    }

    private sealed class NameSelector : Selector
    {
        private readonly string _name;

        public NameSelector(string name) => _name = name;

        public override void Select(JsonNode? node, JsonNode? root, List<JsonNode?> output)
        {
            if (node is JsonObject o && o.TryGetPropertyValue(_name, out var child))
            {
                output.Add(child);
            }
            else if (node is JsonArray a && string.Equals(_name, "length", StringComparison.Ordinal))
            {
                output.Add(JsonValue.Create(a.Count));
            }
        }
    }

    private sealed class WildcardSelector : Selector
    {
        public override void Select(JsonNode? node, JsonNode? root, List<JsonNode?> output)
        {
            if (node is JsonObject o)
            {
                foreach (var kv in o)
                {
                    output.Add(kv.Value);
                }
            }
            else if (node is JsonArray a)
            {
                foreach (var item in a)
                {
                    output.Add(item);
                }
            }
        }
    }

    private sealed class IndexSelector : Selector
    {
        private readonly int _index;

        public IndexSelector(int index) => _index = index;

        public override void Select(JsonNode? node, JsonNode? root, List<JsonNode?> output)
        {
            if (node is JsonArray a)
            {
                var i = _index < 0 ? a.Count + _index : _index;
                if (i >= 0 && i < a.Count)
                {
                    output.Add(a[i]);
                }
            }
        }
    }

    private sealed class SliceSelector : Selector
    {
        private readonly int? _start;
        private readonly int? _end;
        private readonly int _step;

        public SliceSelector(int? start, int? end, int step)
        {
            _start = start;
            _end = end;
            _step = step == 0 ? 1 : step;
        }

        public override void Select(JsonNode? node, JsonNode? root, List<JsonNode?> output)
        {
            if (node is not JsonArray a)
            {
                return;
            }

            var n = a.Count;
            int Norm(int v) => v < 0 ? Math.Max(n + v, 0) : Math.Min(v, n);
            if (_step > 0)
            {
                var start = _start.HasValue ? Norm(_start.Value) : 0;
                var end = _end.HasValue ? Norm(_end.Value) : n;
                for (var i = start; i < end; i += _step)
                {
                    output.Add(a[i]);
                }
            }
            else
            {
                var start = _start.HasValue ? Math.Min(_start.Value < 0 ? n + _start.Value : _start.Value, n - 1) : n - 1;
                var end = _end.HasValue ? (_end.Value < 0 ? n + _end.Value : _end.Value) : -1;
                for (var i = start; i > end && i >= 0; i += _step)
                {
                    output.Add(a[i]);
                }
            }
        }
    }

    private sealed class FilterSelector : Selector
    {
        private readonly FilterExpr _expr;

        public FilterSelector(FilterExpr expr) => _expr = expr;

        public override void Select(JsonNode? node, JsonNode? root, List<JsonNode?> output)
        {
            if (node is JsonArray a)
            {
                foreach (var item in a)
                {
                    if (_expr.Test(item, root))
                    {
                        output.Add(item);
                    }
                }
            }
            else if (node is JsonObject o)
            {
                foreach (var kv in o)
                {
                    if (_expr.Test(kv.Value, root))
                    {
                        output.Add(kv.Value);
                    }
                }
            }
        }
    }

    private abstract class FilterExpr
    {
        public abstract bool Test(JsonNode? current, JsonNode? root);
    }

    private sealed class OrExpr : FilterExpr
    {
        private readonly FilterExpr _a;
        private readonly FilterExpr _b;

        public OrExpr(FilterExpr a, FilterExpr b)
        {
            _a = a;
            _b = b;
        }

        public override bool Test(JsonNode? current, JsonNode? root) => _a.Test(current, root) || _b.Test(current, root);
    }

    private sealed class AndExpr : FilterExpr
    {
        private readonly FilterExpr _a;
        private readonly FilterExpr _b;

        public AndExpr(FilterExpr a, FilterExpr b)
        {
            _a = a;
            _b = b;
        }

        public override bool Test(JsonNode? current, JsonNode? root) => _a.Test(current, root) && _b.Test(current, root);
    }

    private sealed class NotExpr : FilterExpr
    {
        private readonly FilterExpr _a;

        public NotExpr(FilterExpr a) => _a = a;

        public override bool Test(JsonNode? current, JsonNode? root) => !_a.Test(current, root);
    }

    private sealed class Operand
    {
        public JsonPath? Path { get; init; }

        public JsonNode? Literal { get; init; }

        public bool IsLiteral { get; init; }

        public Regex? Regex { get; init; }

        public (bool Exists, JsonNode? Value) Eval(JsonNode? current, JsonNode? root)
        {
            if (IsLiteral)
            {
                return (true, Literal);
            }

            var matches = Path!.Select(root, current);
            return matches.Count > 0 ? (true, matches[0]) : (false, null);
        }
    }

    private sealed class CompareExpr : FilterExpr
    {
        private readonly Operand _left;
        private readonly string? _op;
        private readonly Operand? _right;

        public CompareExpr(Operand left, string? op, Operand? right)
        {
            _left = left;
            _op = op;
            _right = right;
        }

        public override bool Test(JsonNode? current, JsonNode? root)
        {
            var (lExists, l) = _left.Eval(current, root);
            if (_op is null)
            {
                if (!lExists)
                {
                    return false;
                }

                // Existence test; a literal false/null/"" is falsy.
                var text = ToText(l);
                return l is not null && !string.Equals(text, "false", StringComparison.Ordinal);
            }

            if (_op == "=~")
            {
                var s = ToText(l);
                if (s is null || _right?.Regex is null)
                {
                    return false;
                }

                try
                {
                    return _right.Regex.IsMatch(s);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            }

            var (rExists, r) = _right!.Eval(current, root);
            if (!lExists || !rExists)
            {
                return _op == "!=" && lExists != rExists;
            }

            if (TryNumber(l, out var ln) && TryNumber(r, out var rn))
            {
                return _op switch
                {
                    "==" => ln.Equals(rn),
                    "!=" => !ln.Equals(rn),
                    "<" => ln < rn,
                    "<=" => ln <= rn,
                    ">" => ln > rn,
                    ">=" => ln >= rn,
                    _ => false,
                };
            }

            var ls = l is null ? null : ToText(l);
            var rs = r is null ? null : ToText(r);
            var cmp = string.CompareOrdinal(ls, rs);
            return _op switch
            {
                "==" => cmp == 0 && (l is null) == (r is null),
                "!=" => cmp != 0 || (l is null) != (r is null),
                "<" => cmp < 0,
                "<=" => cmp <= 0,
                ">" => cmp > 0,
                ">=" => cmp >= 0,
                _ => false,
            };
        }
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s)
        {
            _s = s.Trim();
        }

        public JsonPath ParsePath()
        {
            var path = ParseInner(stopAtFilterEnd: false);
            SkipWs();
            if (_i != _s.Length)
            {
                throw Error("unexpected character '" + _s[_i] + "'");
            }

            return path;
        }

        private JsonPath ParseInner(bool stopAtFilterEnd)
        {
            var start = _i;
            if (_i >= _s.Length || (_s[_i] != '$' && _s[_i] != '@'))
            {
                throw Error("a path must start with $ or @");
            }

            var relative = _s[_i] == '@';
            _i++;
            var segments = new List<Segment>();
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (c == '.')
                {
                    if (_i + 1 < _s.Length && _s[_i + 1] == '.')
                    {
                        _i += 2;
                        if (_i < _s.Length && _s[_i] == '[')
                        {
                            segments.Add(new DescendantSegment(ParseBracket()));
                        }
                        else if (_i < _s.Length && _s[_i] == '*')
                        {
                            _i++;
                            segments.Add(new DescendantSegment(new Selector[] { new WildcardSelector() }));
                        }
                        else
                        {
                            segments.Add(new DescendantSegment(new Selector[] { new NameSelector(ReadName()) }));
                        }
                    }
                    else
                    {
                        _i++;
                        if (_i < _s.Length && _s[_i] == '*')
                        {
                            _i++;
                            segments.Add(new ChildSegment(new Selector[] { new WildcardSelector() }));
                        }
                        else
                        {
                            segments.Add(new ChildSegment(new Selector[] { new NameSelector(ReadName()) }));
                        }
                    }
                }
                else if (c == '[')
                {
                    segments.Add(new ChildSegment(ParseBracket()));
                }
                else if (stopAtFilterEnd)
                {
                    break;
                }
                else if (char.IsWhiteSpace(c))
                {
                    break;
                }
                else
                {
                    throw Error("unexpected character '" + c + "'");
                }
            }

            return new JsonPath(_s[start.._i], relative, segments.ToArray());
        }

        private string ReadName()
        {
            var start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '-' || _s[_i] == '$' || _s[_i] > 127))
            {
                _i++;
            }

            if (start == _i)
            {
                throw Error("member name expected");
            }

            return _s[start.._i];
        }

        private Selector[] ParseBracket()
        {
            Expect('[');
            var selectors = new List<Selector>();
            while (true)
            {
                SkipWs();
                selectors.Add(ParseSelector());
                SkipWs();
                if (Peek() == ',')
                {
                    _i++;
                    continue;
                }

                Expect(']');
                return selectors.ToArray();
            }
        }

        private Selector ParseSelector()
        {
            var c = Peek();
            if (c == '*')
            {
                _i++;
                return new WildcardSelector();
            }

            if (c == '\'' || c == '"')
            {
                return new NameSelector(ReadQuoted());
            }

            if (c == '?')
            {
                _i++;
                SkipWs();
                var paren = Peek() == '(';
                if (paren)
                {
                    _i++;
                }

                var expr = ParseOr();
                SkipWs();
                if (paren)
                {
                    Expect(')');
                }

                return new FilterSelector(expr);
            }

            // index or slice
            int? a = TryReadInt();
            SkipWs();
            if (Peek() == ':')
            {
                _i++;
                SkipWs();
                int? b = TryReadInt();
                SkipWs();
                var step = 1;
                if (Peek() == ':')
                {
                    _i++;
                    SkipWs();
                    step = TryReadInt() ?? 1;
                }

                return new SliceSelector(a, b, step);
            }

            if (a is null)
            {
                throw Error("selector expected");
            }

            return new IndexSelector(a.Value);
        }

        private FilterExpr ParseOr()
        {
            var left = ParseAnd();
            while (true)
            {
                SkipWs();
                if (Match("||"))
                {
                    left = new OrExpr(left, ParseAnd());
                }
                else
                {
                    return left;
                }
            }
        }

        private FilterExpr ParseAnd()
        {
            var left = ParseUnary();
            while (true)
            {
                SkipWs();
                if (Match("&&"))
                {
                    left = new AndExpr(left, ParseUnary());
                }
                else
                {
                    return left;
                }
            }
        }

        private FilterExpr ParseUnary()
        {
            SkipWs();
            if (Peek() == '!' && (_i + 1 >= _s.Length || _s[_i + 1] != '='))
            {
                _i++;
                return new NotExpr(ParseUnary());
            }

            if (Peek() == '(')
            {
                _i++;
                var inner = ParseOr();
                SkipWs();
                Expect(')');
                return inner;
            }

            var left = ParseOperand(allowRegex: false);
            SkipWs();
            string? op = null;
            foreach (var candidate in new[] { "==", "!=", "<=", ">=", "=~", "<", ">" })
            {
                if (Match(candidate))
                {
                    op = candidate;
                    break;
                }
            }

            if (op is null)
            {
                return new CompareExpr(left, null, null);
            }

            SkipWs();
            var right = ParseOperand(allowRegex: op == "=~");
            return new CompareExpr(left, op, right);
        }

        private Operand ParseOperand(bool allowRegex)
        {
            SkipWs();
            var c = Peek();
            if (c == '@' || c == '$')
            {
                return new Operand { Path = ParseInner(stopAtFilterEnd: true) };
            }

            if (c == '\'' || c == '"')
            {
                return new Operand { IsLiteral = true, Literal = JsonValue.Create(ReadQuoted()) };
            }

            if (allowRegex && c == '/')
            {
                _i++;
                var sb = new StringBuilder();
                while (_i < _s.Length && _s[_i] != '/')
                {
                    if (_s[_i] == '\\' && _i + 1 < _s.Length)
                    {
                        sb.Append(_s[_i]).Append(_s[_i + 1]);
                        _i += 2;
                        continue;
                    }

                    sb.Append(_s[_i++]);
                }

                Expect('/');
                var options = RegexOptions.CultureInvariant;
                while (_i < _s.Length && char.IsLetter(_s[_i]))
                {
                    options |= _s[_i] switch
                    {
                        'i' => RegexOptions.IgnoreCase,
                        'm' => RegexOptions.Multiline,
                        's' => RegexOptions.Singleline,
                        _ => throw Error("unknown regex flag"),
                    };
                    _i++;
                }

                try
                {
                    return new Operand { IsLiteral = true, Regex = new Regex(sb.ToString(), options, RegexTimeout) };
                }
                catch (ArgumentException ex)
                {
                    throw Error("invalid regex: " + ex.Message);
                }
            }

            if (Match("true"))
            {
                return new Operand { IsLiteral = true, Literal = JsonValue.Create(true) };
            }

            if (Match("false"))
            {
                return new Operand { IsLiteral = true, Literal = JsonValue.Create(false) };
            }

            if (Match("null"))
            {
                return new Operand { IsLiteral = true, Literal = null };
            }

            var start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '-' || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E' || _s[_i] == '+'))
            {
                _i++;
            }

            if (start != _i && double.TryParse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                return new Operand { IsLiteral = true, Literal = JsonValue.Create(d) };
            }

            throw Error("operand expected");
        }

        private string ReadQuoted()
        {
            var quote = _s[_i++];
            var sb = new StringBuilder();
            while (_i < _s.Length && _s[_i] != quote)
            {
                if (_s[_i] == '\\' && _i + 1 < _s.Length)
                {
                    _i++;
                    sb.Append(_s[_i] switch { 'n' => '\n', 't' => '\t', _ => _s[_i] });
                    _i++;
                    continue;
                }

                sb.Append(_s[_i++]);
            }

            Expect(quote);
            return sb.ToString();
        }

        private int? TryReadInt()
        {
            var start = _i;
            if (Peek() == '-')
            {
                _i++;
            }

            while (_i < _s.Length && char.IsDigit(_s[_i]))
            {
                _i++;
            }

            if (_i == start || (_i == start + 1 && _s[start] == '-'))
            {
                _i = start;
                return null;
            }

            return int.Parse(_s.AsSpan(start, _i - start), NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private bool Match(string token)
        {
            if (string.CompareOrdinal(_s, _i, token, 0, token.Length) == 0)
            {
                _i += token.Length;
                return true;
            }

            return false;
        }

        private char Peek() => _i < _s.Length ? _s[_i] : '\0';

        private void Expect(char c)
        {
            if (Peek() != c)
            {
                throw Error($"'{c}' expected");
            }

            _i++;
        }

        private void SkipWs()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
            {
                _i++;
            }
        }

        private FormatException Error(string message)
            => new($"Invalid JSONPath '{_s}' at {_i}: {message}.");
    }
}
