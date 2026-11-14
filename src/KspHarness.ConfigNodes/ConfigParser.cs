using System;
using System.Collections.Generic;

namespace KspHarness.ConfigNodes;

/// <summary>
/// Reads config text the way KSP 1.12.5's <c>ConfigNode.Parse</c> and <c>ConfigNode.Load</c> do.
/// </summary>
/// <remarks>
/// KSP's rules, which this follows edge case for edge case:
/// <list type="bullet">
/// <item>Everything from the first <c>//</c> on a line is a comment, even inside a value (<c>url = http://x</c>
/// reads as <c>http:</c>).</item>
/// <item>Every <c>{</c> and <c>}</c> on a line is a token of its own, wherever it appears, values included. KSP's
/// writer turns braces in values into square brackets so this never happens in files it saves.</item>
/// <item>A token with an <c>=</c> is a value: the key is the text before the first <c>=</c> and the value the rest,
/// both trimmed. Further <c>=</c> signs stay in the value.</item>
/// <item>A token without <c>=</c> names a node only when the next token is <c>{</c>; otherwise it is dropped. A
/// <c>{</c> with no name before it opens a node with an empty name.</item>
/// <item>A <c>}</c> closes the current node. A stray <c>}</c> at the top level ends the file: KSP ignores the rest.
/// A missing <c>}</c> closes the node at the end of the file.</item>
/// </list>
/// </remarks>
public static class ConfigParser
{
    /// <summary>The name KSP gives the node that holds a file's top-level values and nodes.</summary>
    public const string RootName = "root";

    /// <summary>Parses text into a root node holding the file's top-level values and nodes.</summary>
    public static CfgNode Parse(string text)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text));

        if (text.Length > 0 && text[0] == '﻿')
            text = text.Substring(1);

        return ParseLines(text.Split('\n', '\r'));
    }

    /// <summary>Parses lines into a root node holding their top-level values and nodes.</summary>
    public static CfgNode ParseLines(IEnumerable<string> lines)
    {
        var tokens = Tokenize(lines);
        var root = new CfgNode(RootName);
        var index = 0;
        ReadNode(tokens, ref index, root);
        return root;
    }

    private static List<Token> Tokenize(IEnumerable<string> lines)
    {
        var tokens = new List<Token>();
        foreach (var rawLine in lines)
        {
            var line = rawLine;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                line = line.Substring(0, comment);

            var start = 0;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c != '{' && c != '}')
                    continue;

                AddText(tokens, line.Substring(start, i - start));
                tokens.Add(c == '{' ? Token.Open : Token.Close);
                start = i + 1;
            }

            AddText(tokens, line.Substring(start));
        }

        return tokens;
    }

    private static void AddText(List<Token> tokens, string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;

        var equals = text.IndexOf('=');
        if (equals < 0)
        {
            tokens.Add(new Token(TokenKind.Name, text, null));
            return;
        }

        var key = text.Substring(0, equals).Trim();
        var value = text.Substring(equals + 1).Trim();
        tokens.Add(new Token(TokenKind.Value, key, value));
    }

    private static void ReadNode(List<Token> tokens, ref int index, CfgNode node)
    {
        while (index < tokens.Count)
        {
            var token = tokens[index];
            switch (token.Kind)
            {
                case TokenKind.Value:
                    node.ValueList.Add(new CfgValue(token.Text, token.Value!));
                    index++;
                    break;

                case TokenKind.Open:
                    var unnamed = node.AddNode(string.Empty);
                    index++;
                    ReadNode(tokens, ref index, unnamed);
                    break;

                case TokenKind.Close:
                    index++;
                    return;

                default:
                    if (index + 1 < tokens.Count && tokens[index + 1].Kind == TokenKind.Open)
                    {
                        var child = node.AddNode(token.Text);
                        index += 2;
                        ReadNode(tokens, ref index, child);
                    }
                    else
                    {
                        index++;
                    }

                    break;
            }
        }
    }

    private enum TokenKind
    {
        Name,
        Value,
        Open,
        Close,
    }

    private sealed class Token
    {
        public static readonly Token Open = new Token(TokenKind.Open, "{", null);

        public static readonly Token Close = new Token(TokenKind.Close, "}", null);

        public Token(TokenKind kind, string text, string? value)
        {
            Kind = kind;
            Text = text;
            Value = value;
        }

        public TokenKind Kind { get; }

        public string Text { get; }

        public string? Value { get; }
    }
}
