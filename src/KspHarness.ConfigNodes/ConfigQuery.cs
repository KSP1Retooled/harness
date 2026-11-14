using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KspHarness.ConfigNodes;

/// <summary>
/// A query over config nodes in ModuleManager's node syntax. See the README for the grammar.
/// </summary>
/// <remarks>
/// <para>A query is a path of segments separated by <c>/</c> (children) or <c>//</c> (descendants at any depth). A
/// node segment is <c>TYPE[names]:HAS[...],index</c>, where every part but the type is optional:</para>
/// <list type="bullet">
/// <item>TYPE matches the node's name with <c>*</c> and <c>?</c> wildcards.</item>
/// <item><c>[names]</c> matches the node's <c>name</c> value: wildcard patterns separated by <c>|</c> or
/// <c>,</c>, any one of which may match. A node without a <c>name</c> value never matches.</item>
/// <item><c>:HAS[...]</c> constraints, with ModuleManager's semantics (whitespace removed, commas or <c>&amp;</c>
/// as AND): <c>@NODE[name]</c> has a matching child, <c>!NODE[name]</c> has none, <c>#key[pattern]</c> has a value
/// matching the pattern (<c>&gt;n</c> and <c>&lt;n</c> compare numbers), <c>~key[]</c> lacks the key and
/// <c>~key[pattern]</c> has no value matching the pattern. Each may carry its own <c>:HAS[...]</c>.</item>
/// <item><c>,N</c> keeps the Nth match among siblings (negative counts from the end); <c>,*</c> or no index keeps
/// all.</item>
/// </list>
/// <para><see cref="SelectValues{TNode}(TNode, INodeAdapter{TNode})"/> reads the last segment as a key pattern
/// (optionally written <c>#key</c>) with an optional <c>,N</c> index.</para>
/// </remarks>
public sealed class ConfigQuery
{
    private readonly List<Segment> segments;

    private ConfigQuery(string text, List<Segment> segments)
    {
        Text = text;
        this.segments = segments;
    }

    public string Text { get; }

    public static ConfigQuery Parse(string text)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text));

        return new ConfigQuery(text, ParsePath(text));
    }

    public override string ToString()
    {
        return Text;
    }

    /// <summary>Nodes selected starting from the children of <paramref name="context"/>.</summary>
    public List<TNode> SelectNodes<TNode>(TNode context, INodeAdapter<TNode> adapter)
    {
        return Evaluate(Children(context, adapter), segments.Count, adapter);
    }

    /// <summary>
    /// Nodes selected starting from a list of top-level nodes, which the first segment matches: the nodes of a
    /// <c>.cfg</c> file or of the game database.
    /// </summary>
    public List<TNode> SelectNodes<TNode>(IEnumerable<TNode> topLevel, INodeAdapter<TNode> adapter)
    {
        return Evaluate(new List<TNode>(topLevel), segments.Count, adapter);
    }

    /// <summary>Values selected starting from <paramref name="context"/>: the last segment is a key.</summary>
    public List<ConfigValue<TNode>> SelectValues<TNode>(TNode context, INodeAdapter<TNode> adapter)
    {
        var owners = segments.Count == 1 ? new List<TNode> { context } : Evaluate(Children(context, adapter), segments.Count - 1, adapter);
        return CollectValues(owners, adapter);
    }

    /// <summary>Values selected starting from a list of top-level nodes: the last segment is a key.</summary>
    public List<ConfigValue<TNode>> SelectValues<TNode>(IEnumerable<TNode> topLevel, INodeAdapter<TNode> adapter)
    {
        var top = new List<TNode>(topLevel);
        if (segments.Count == 1)
            return segments[0].Deep ? CollectValues(top, adapter) : new List<ConfigValue<TNode>>();

        return CollectValues(Evaluate(top, segments.Count - 1, adapter), adapter);
    }

    private List<TNode> Evaluate<TNode>(List<TNode> firstLevel, int count, INodeAdapter<TNode> adapter)
    {
        var current = new List<TNode>();
        var seen = new HashSet<TNode>();
        Collect(firstLevel, segments[0], adapter, current, seen);

        for (var i = 1; i < count; i++)
        {
            var next = new List<TNode>();
            seen.Clear();
            foreach (var node in current)
                Collect(Children(node, adapter), segments[i], adapter, next, seen);

            current = next;
        }

        return current;
    }

    private static void Collect<TNode>(List<TNode> siblings, Segment segment, INodeAdapter<TNode> adapter, List<TNode> output, HashSet<TNode> seen)
    {
        var matches = new List<int>();
        for (var i = 0; i < siblings.Count; i++)
        {
            if (segment.Matches(siblings[i], adapter))
                matches.Add(i);
        }

        var selected = new bool[siblings.Count];
        foreach (var index in segment.Pick(matches))
            selected[index] = true;

        for (var i = 0; i < siblings.Count; i++)
        {
            if (selected[i] && seen.Add(siblings[i]))
                output.Add(siblings[i]);

            if (segment.Deep)
                Collect(Children(siblings[i], adapter), segment, adapter, output, seen);
        }
    }

    private List<ConfigValue<TNode>> CollectValues<TNode>(List<TNode> owners, INodeAdapter<TNode> adapter)
    {
        var segment = segments[segments.Count - 1];
        if (segment.Names != null || segment.Constraints.Count > 0)
            throw new FormatException($"the key segment '{segment.Text}' of '{Text}' cannot have [names] or :HAS");

        var result = new List<ConfigValue<TNode>>();
        var seen = new HashSet<TNode>();
        foreach (var owner in owners)
            CollectValues(owner, segment, adapter, result, seen);

        return result;
    }

    private static void CollectValues<TNode>(TNode node, Segment segment, INodeAdapter<TNode> adapter, List<ConfigValue<TNode>> output, HashSet<TNode> seen)
    {
        if (seen.Add(node))
        {
            var matches = new List<int>();
            var count = adapter.GetValueCount(node);
            for (var i = 0; i < count; i++)
            {
                if (Wildcard.IsMatch(segment.Type, adapter.GetValueKey(node, i)))
                    matches.Add(i);
            }

            foreach (var index in segment.Pick(matches))
                output.Add(new ConfigValue<TNode>(node, index, adapter.GetValueKey(node, index), adapter.GetValueText(node, index)));
        }

        if (!segment.Deep)
            return;

        var children = adapter.GetNodeCount(node);
        for (var i = 0; i < children; i++)
            CollectValues(adapter.GetNode(node, i), segment, adapter, output, seen);
    }

    private static List<TNode> Children<TNode>(TNode node, INodeAdapter<TNode> adapter)
    {
        var count = adapter.GetNodeCount(node);
        var children = new List<TNode>(count);
        for (var i = 0; i < count; i++)
            children.Add(adapter.GetNode(node, i));

        return children;
    }

    private static List<Segment> ParsePath(string text)
    {
        var result = new List<Segment>();
        var deep = false;
        var start = 0;
        var depth = 0;
        var i = 0;

        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            deep = true;
            i = start = 2;
        }
        else if (text.StartsWith("/", StringComparison.Ordinal))
        {
            i = start = 1;
        }

        for (; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : '/';
            if (c == '[')
                depth++;
            else if (c == ']')
                depth--;

            if (depth < 0)
                throw new FormatException($"unbalanced ']' at {i} in '{text}'");

            if (c != '/' || depth > 0)
                continue;

            result.Add(Segment.Parse(text.Substring(start, i - start), deep, text));
            deep = i + 1 < text.Length && text[i + 1] == '/';
            if (deep)
                i++;

            start = i + 1;
        }

        if (depth != 0)
            throw new FormatException($"unbalanced '[' in '{text}'");

        return result;
    }

    private sealed class Segment
    {
        private Segment(string text, bool deep)
        {
            Text = text;
            Deep = deep;
        }

        public string Text { get; }

        public bool Deep { get; }

        public string Type { get; private set; } = string.Empty;

        public string[]? Names { get; private set; }

        public List<Constraint> Constraints { get; } = new List<Constraint>();

        /// <summary>The sibling index to keep, or null for all.</summary>
        public int? Index { get; private set; }

        public static Segment Parse(string text, bool deep, string query)
        {
            var segment = new Segment(text.Trim(), deep);
            var rest = segment.Text;
            if (rest.Length == 0)
                throw new FormatException($"empty segment in '{query}'");

            if (rest[0] == '#')
                rest = rest.Substring(1);

            var typeEnd = 0;
            while (typeEnd < rest.Length && rest[typeEnd] != '[' && rest[typeEnd] != ':' && rest[typeEnd] != ',')
                typeEnd++;

            segment.Type = rest.Substring(0, typeEnd).Trim();
            if (segment.Type.Length == 0)
                throw new FormatException($"segment '{text}' of '{query}' has no node type or key");

            rest = rest.Substring(typeEnd);
            if (rest.StartsWith("[", StringComparison.Ordinal))
            {
                var close = MatchingBracket(rest, 0, query);
                segment.Names = rest.Substring(1, close - 1).Split('|', ',');
                rest = rest.Substring(close + 1);
            }

            while (rest.StartsWith(":HAS[", StringComparison.OrdinalIgnoreCase))
            {
                var close = MatchingBracket(rest, 4, query);
                segment.Constraints.AddRange(Constraint.ParseList(rest.Substring(5, close - 5), query));
                rest = rest.Substring(close + 1);
            }

            if (rest.StartsWith(",", StringComparison.Ordinal))
            {
                var index = rest.Substring(1).Trim();
                if (index != "*")
                {
                    if (!int.TryParse(index, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                        throw new FormatException($"bad index '{index}' in '{query}'");

                    segment.Index = value;
                }

                rest = string.Empty;
            }

            if (rest.Trim().Length > 0)
                throw new FormatException($"unexpected '{rest}' in segment '{text}' of '{query}'");

            return segment;
        }

        public bool Matches<TNode>(TNode node, INodeAdapter<TNode> adapter)
        {
            if (!Wildcard.IsMatch(Type, adapter.GetName(node)))
                return false;

            if (!NameMatches(Names, node, adapter))
                return false;

            foreach (var constraint in Constraints)
            {
                if (!constraint.IsMet(node, adapter))
                    return false;
            }

            return true;
        }

        public IEnumerable<int> Pick(List<int> matches)
        {
            if (Index == null)
                return matches;

            var index = Index.Value < 0 ? matches.Count + Index.Value : Index.Value;
            return index >= 0 && index < matches.Count ? new[] { matches[index] } : new int[0];
        }
    }

    private sealed class Constraint
    {
        private Constraint(char op, string type, string? name)
        {
            Op = op;
            Type = type;
            Name = name;
        }

        public char Op { get; }

        public string Type { get; }

        /// <summary>The bracketed pattern; null when the constraint has no brackets.</summary>
        public string? Name { get; }

        public List<Constraint> Then { get; } = new List<Constraint>();

        public static List<Constraint> ParseList(string text, string query)
        {
            // ModuleManager strips all whitespace from HAS blocks, so patterns match names with spaces only through ?.
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (!char.IsWhiteSpace(c))
                    builder.Append(c);
            }

            var compact = builder.ToString();
            var result = new List<Constraint>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i <= compact.Length; i++)
            {
                var c = i < compact.Length ? compact[i] : ',';
                if (c == '[')
                    depth++;
                else if (c == ']')
                    depth--;
                else if ((c == ',' || c == '&') && depth == 0)
                {
                    if (i > start)
                        result.Add(ParseOne(compact.Substring(start, i - start), query));

                    start = i + 1;
                }
            }

            return result;
        }

        private static Constraint ParseOne(string text, string query)
        {
            var head = text;
            string? inner = null;
            var has = IndexOfHas(text);
            if (has >= 0)
            {
                var close = MatchingBracket(text, has + 4, query);
                if (close != text.Length - 1)
                    throw new FormatException($"unexpected text after :HAS[...] in '{text}' of '{query}'");

                head = text.Substring(0, has);
                inner = text.Substring(has + 5, close - has - 5);
            }

            var op = head.Length > 0 ? head[0] : ' ';
            if (op != '@' && op != '!' && op != '#' && op != '~')
                throw new FormatException($"HAS constraint '{text}' of '{query}' must start with @, !, # or ~");

            var parts = head.Substring(1).Split(new[] { '[', ']' }, 3);
            var constraint = new Constraint(op, parts[0], parts.Length > 1 ? parts[1] : null);
            if (inner != null)
                constraint.Then.AddRange(ParseList(inner, query));

            return constraint;
        }

        private static int IndexOfHas(string text)
        {
            var depth = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '[')
                    depth++;
                else if (text[i] == ']')
                    depth--;
                else if (depth == 0 && string.CompareOrdinal(text, i, ":HAS[", 0, 5) == 0)
                    return i;
            }

            return -1;
        }

        public bool IsMet<TNode>(TNode node, INodeAdapter<TNode> adapter)
        {
            switch (Op)
            {
                case '@':
                    return HasMatchingChild(node, adapter);
                case '!':
                    return !HasMatchingChild(node, adapter);
                case '#':
                    return HasKey(node, adapter) && AnyValueMatches(node, adapter) && AllMet(Then, node, adapter);
                default:
                    if (Name == string.Empty && HasKey(node, adapter))
                        return false;

                    if (Name != string.Empty && AnyValueMatches(node, adapter))
                        return false;

                    return AllMet(Then, node, adapter);
            }
        }

        private bool HasMatchingChild<TNode>(TNode node, INodeAdapter<TNode> adapter)
        {
            var names = Name?.Split('|');
            var count = adapter.GetNodeCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = adapter.GetNode(node, i);
                if (Wildcard.IsMatch(Type, adapter.GetName(child)) && NameMatches(names, child, adapter) && AllMet(Then, child, adapter))
                    return true;
            }

            return false;
        }

        private bool HasKey<TNode>(TNode node, INodeAdapter<TNode> adapter)
        {
            var count = adapter.GetValueCount(node);
            for (var i = 0; i < count; i++)
            {
                if (adapter.GetValueKey(node, i) == Type)
                    return true;
            }

            return false;
        }

        private bool AnyValueMatches<TNode>(TNode node, INodeAdapter<TNode> adapter)
        {
            var compare = 0;
            var limit = 0.0;
            if (Name != null && Name.Length > 1 && (Name[0] == '<' || Name[0] == '>')
                && double.TryParse(Name.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out limit))
            {
                compare = Name[0] == '<' ? -1 : 1;
            }

            var count = adapter.GetValueCount(node);
            for (var i = 0; i < count; i++)
            {
                if (adapter.GetValueKey(node, i) != Type)
                    continue;

                var text = adapter.GetValueText(node, i);
                if (compare != 0)
                {
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        && (compare < 0 ? number < limit : number > limit))
                    {
                        return true;
                    }
                }
                else if (Name == null || Wildcard.IsMatch(Name, text))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool AllMet<TNode>(List<Constraint> constraints, TNode node, INodeAdapter<TNode> adapter)
        {
            foreach (var constraint in constraints)
            {
                if (!constraint.IsMet(node, adapter))
                    return false;
            }

            return true;
        }
    }

    private static bool NameMatches<TNode>(string[]? patterns, TNode node, INodeAdapter<TNode> adapter)
    {
        if (patterns == null)
            return true;

        string? name = null;
        var count = adapter.GetValueCount(node);
        for (var i = 0; i < count && name == null; i++)
        {
            if (adapter.GetValueKey(node, i) == "name")
                name = adapter.GetValueText(node, i);
        }

        if (name == null)
            return false;

        foreach (var pattern in patterns)
        {
            if (Wildcard.IsMatch(pattern, name))
                return true;
        }

        return false;
    }

    /// <summary>The index of the <c>]</c> that closes the <c>[</c> at <paramref name="open"/>.</summary>
    private static int MatchingBracket(string text, int open, string query)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '[')
                depth++;
            else if (text[i] == ']' && --depth == 0)
                return i;
        }

        throw new FormatException($"unbalanced '[' in '{query}'");
    }
}
