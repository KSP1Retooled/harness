using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace KspHarness.Snippets;

/// <summary>The source text of a lambda passed to <c>GameSession.Run</c>, as the snippet compiler needs it.</summary>
internal static class LambdaText
{
    /// <summary>
    /// Checks that the text is a lambda with no parameters and returns it unchanged.
    /// </summary>
    public static string ParameterlessLambda(string text, SourcePosition position)
    {
        var lambda = ParseLambda(text, position);
        if (lambda is not ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 0 }
            && lambda is not AnonymousMethodExpressionSyntax { ParameterList: null or { Parameters.Count: 0 } })
        {
            throw new SnippetCompileException("Game.Run takes a lambda with no parameters here", position.File, position.Line);
        }

        return text;
    }

    /// <summary>
    /// Removes the dynamic parameter of <c>ksp =&gt; ksp.FlightGlobals.ActiveVessel</c>: each member access on it
    /// becomes a plain name, so the lambda turns into <c>() =&gt; FlightGlobals.ActiveVessel</c>. Line breaks are
    /// kept so that line mapping still holds.
    /// </summary>
    public static string RemoveDynamicParameter(string text, SourcePosition position)
    {
        var lambda = ParseLambda(text, position);
        SyntaxToken parameter;
        CSharpSyntaxNode body;
        SyntaxToken arrow;
        SyntaxToken asyncKeyword;
        switch (lambda)
        {
            case SimpleLambdaExpressionSyntax simple:
                parameter = simple.Parameter.Identifier;
                body = simple.Body;
                arrow = simple.ArrowToken;
                asyncKeyword = simple.AsyncKeyword;
                break;
            case ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized:
                parameter = parenthesized.ParameterList.Parameters[0].Identifier;
                body = parenthesized.Body;
                arrow = parenthesized.ArrowToken;
                asyncKeyword = parenthesized.AsyncKeyword;
                break;
            default:
                throw new SnippetCompileException(
                    "Game.Run takes a lambda with one parameter here, such as ksp => ksp.FlightGlobals.ActiveVessel",
                    position.File,
                    position.Line);
        }

        var rewriter = new ParameterRemover(parameter.ValueText);
        var rewritten = (CSharpSyntaxNode)rewriter.Visit(body);
        if (rewriter.OtherUse is { } use)
        {
            var line = position.Line + use.GetLocation().GetLineSpan().StartLinePosition.Line;
            throw new SnippetCompileException(
                $"'{parameter.ValueText}' stands for the game's namespace and can only be followed by a member, as in "
                + $"{parameter.ValueText}.FlightGlobals; '{use.Parent}' uses it another way",
                position.File,
                line);
        }

        var prefix = asyncKeyword.IsKind(SyntaxKind.AsyncKeyword) ? "async () =>" : "() =>";
        return prefix + arrow.TrailingTrivia.ToFullString() + rewritten.ToFullString();
    }

    /// <summary>The simple names the code uses, which are the only captured variables worth sending.</summary>
    public static HashSet<string> UsedNames(string code)
    {
        var root = SyntaxFactory.ParseExpression(code);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in root.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            // The right side of a member access names a member, not a variable.
            if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name)
            {
                continue;
            }

            names.Add(name.Identifier.ValueText);
        }

        return names;
    }

    /// <summary>Whether the code says <c>this</c> or <c>base</c>.</summary>
    public static bool UsesThis(string code)
    {
        var root = SyntaxFactory.ParseExpression(code);
        return root.DescendantNodesAndSelf().Any(n => n is ThisExpressionSyntax or BaseExpressionSyntax);
    }

    private static AnonymousFunctionExpressionSyntax ParseLambda(string text, SourcePosition position)
    {
        var expression = SyntaxFactory.ParseExpression(text);
        if (expression is not AnonymousFunctionExpressionSyntax lambda)
        {
            throw new SnippetCompileException(
                $"Game.Run needs a lambda written at the call, so that its source can be sent; got '{text}'",
                position.File,
                position.Line);
        }

        return lambda;
    }

    private sealed class ParameterRemover(string parameter) : CSharpSyntaxRewriter
    {
        public SyntaxNode? OtherUse { get; private set; }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (node.Expression is IdentifierNameSyntax receiver && receiver.Identifier.ValueText == parameter)
            {
                // The receiver's leading trivia (a line break and indentation) now belongs to the member name.
                var name = (SimpleNameSyntax)Visit(node.Name)!;
                return name.WithLeadingTrivia(node.GetLeadingTrivia()).WithTrailingTrivia(node.GetTrailingTrivia());
            }

            return base.VisitMemberAccessExpression(node);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var isMemberName = node.Parent is MemberAccessExpressionSyntax access && access.Name == node;
            if (node.Identifier.ValueText == parameter && !isMemberName && OtherUse is null)
            {
                OtherUse = node;
            }

            return base.VisitIdentifierName(node);
        }
    }
}
