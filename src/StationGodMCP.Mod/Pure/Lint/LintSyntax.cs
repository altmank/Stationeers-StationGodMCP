#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StationGodMCP.Pure.Lint;

/// <summary>An expression that does not parse or type-check, with the 0-based character it is about.</summary>
internal sealed class LintSyntaxException : Exception
{
    internal LintSyntaxException(string message, int position)
        : base(message)
    {
        Position = position;
    }

    internal int Position { get; }
}

internal enum TokenKind
{
    Number,
    String,
    Name,
    Symbol,
    End
}

internal readonly struct LintToken
{
    internal LintToken(TokenKind kind, string text, int position, double number = 0)
    {
        Kind = kind;
        Text = text;
        Position = position;
        Number = number;
    }

    internal TokenKind Kind { get; }

    internal string Text { get; }

    internal int Position { get; }

    internal double Number { get; }

    internal bool Is(string symbol) => (Kind == TokenKind.Symbol || Kind == TokenKind.Name) && Text == symbol;

    public override string ToString() => Kind == TokenKind.End ? "the end" : $"'{Text}'";
}

/// <summary>Splits an expression into numbers, strings ('...' or "..."), names and symbols.</summary>
internal static class LintLexer
{
    private static readonly string[] Symbols =
        { "?.", "??", "==", "!=", "<=", ">=", "=>", "<", ">", "+", "-", "*", "/", "%", "(", ")", "[", "]", ",", ".", ":", "=" };

    internal static List<LintToken> Tokens(string source)
    {
        List<LintToken> tokens = new List<LintToken>();
        int at = 0;
        while (at < source.Length)
        {
            char c = source[at];
            if (char.IsWhiteSpace(c))
            {
                at++;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && at + 1 < source.Length && char.IsDigit(source[at + 1])))
            {
                int start = at;
                while (at < source.Length && (char.IsDigit(source[at]) || source[at] == '.' ||
                                              source[at] == 'e' || source[at] == 'E' ||
                                              ((source[at] == '-' || source[at] == '+') &&
                                               (source[at - 1] == 'e' || source[at - 1] == 'E'))))
                {
                    at++;
                }

                string text = source.Substring(start, at - start);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    throw new LintSyntaxException($"'{text}' is not a number", start);
                }

                tokens.Add(new LintToken(TokenKind.Number, text, start, number));
                continue;
            }

            if (c == '"' || c == '\'')
            {
                tokens.Add(StringAt(source, ref at));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = at;
                while (at < source.Length && (char.IsLetterOrDigit(source[at]) || source[at] == '_'))
                {
                    at++;
                }

                tokens.Add(new LintToken(TokenKind.Name, source.Substring(start, at - start), start));
                continue;
            }

            string? symbol = null;
            foreach (string candidate in Symbols)
            {
                if (string.CompareOrdinal(source, at, candidate, 0, candidate.Length) == 0)
                {
                    symbol = candidate;
                    break;
                }
            }

            if (symbol == null)
            {
                throw new LintSyntaxException($"unexpected character '{c}'", at);
            }

            tokens.Add(new LintToken(TokenKind.Symbol, symbol, at));
            at += symbol.Length;
        }

        tokens.Add(new LintToken(TokenKind.End, string.Empty, source.Length));
        return tokens;
    }

    private static LintToken StringAt(string source, ref int at)
    {
        char quote = source[at];
        int start = at++;
        StringBuilder text = new StringBuilder();
        while (at < source.Length && source[at] != quote)
        {
            if (source[at] == '\\' && at + 1 < source.Length)
            {
                at++;
                text.Append(source[at] == 'n' ? '\n' : source[at] == 't' ? '\t' : source[at]);
            }
            else
            {
                text.Append(source[at]);
            }

            at++;
        }

        if (at >= source.Length)
        {
            throw new LintSyntaxException("a string is not closed", start);
        }

        at++;
        return new LintToken(TokenKind.String, text.ToString(), start);
    }
}

/// <summary>A parsed expression.</summary>
internal abstract class LintNode
{
    protected LintNode(int position)
    {
        Position = position;
    }

    /// <summary>0-based character in the source.</summary>
    internal int Position { get; }

    /// <summary>The node as source text, for explain and for narrowing (has(x.room) then x.room).</summary>
    internal abstract string Text { get; }
}

internal sealed class LiteralNode : LintNode
{
    internal LiteralNode(LintValue value, LintType type, int position)
        : base(position)
    {
        Value = value;
        Type = type;
    }

    internal LintValue Value { get; }

    internal LintType Type { get; }

    internal override string Text => Value.Kind == LintKind.String ? $"\"{Value.AsString}\"" : Value.ToText();
}

internal sealed class NameNode : LintNode
{
    internal NameNode(string name, int position)
        : base(position)
    {
        Name = name;
    }

    internal string Name { get; }

    internal override string Text => Name;
}

internal sealed class MemberNode : LintNode
{
    internal MemberNode(LintNode target, string name, bool nullSafe, int position)
        : base(position)
    {
        Target = target;
        Name = name;
        NullSafe = nullSafe;
    }

    internal LintNode Target { get; }

    internal string Name { get; }

    internal bool NullSafe { get; }

    internal override string Text => Target.Text + (NullSafe ? "?." : ".") + Name;
}

internal sealed class IndexNode : LintNode
{
    internal IndexNode(LintNode target, LintNode index, int position)
        : base(position)
    {
        Target = target;
        Index = index;
    }

    internal LintNode Target { get; }

    internal LintNode Index { get; }

    internal override string Text => $"{Target.Text}[{Index.Text}]";
}

/// <summary>A call: name(args), or receiver.name(args) which is name(receiver, args).</summary>
internal sealed class CallNode : LintNode
{
    internal CallNode(string name, List<LintNode> args, Dictionary<string, LintNode> named, bool nullSafeReceiver,
        int position)
        : base(position)
    {
        Name = name;
        Args = args;
        Named = named;
        NullSafeReceiver = nullSafeReceiver;
    }

    internal string Name { get; }

    internal List<LintNode> Args { get; }

    internal Dictionary<string, LintNode> Named { get; }

    /// <summary>receiver?.name(...): null when the receiver (the first argument) is null.</summary>
    internal bool NullSafeReceiver { get; }

    internal override string Text
    {
        get
        {
            StringBuilder text = new StringBuilder(Name).Append('(');
            for (int index = 0; index < Args.Count; index++)
            {
                text.Append(index > 0 ? ", " : string.Empty).Append(Args[index].Text);
            }

            return text.Append(')').ToString();
        }
    }
}

internal sealed class LambdaNode : LintNode
{
    internal LambdaNode(string parameter, LintNode body, int position)
        : base(position)
    {
        Parameter = parameter;
        Body = body;
    }

    internal string Parameter { get; }

    internal LintNode Body { get; }

    internal override string Text => $"{Parameter} => {Body.Text}";
}

internal sealed class UnaryNode : LintNode
{
    internal UnaryNode(string op, LintNode operand, int position)
        : base(position)
    {
        Op = op;
        Operand = operand;
    }

    internal string Op { get; }

    internal LintNode Operand { get; }

    internal override string Text => Op == "not" ? $"not {Operand.Text}" : Op + Operand.Text;
}

internal sealed class BinaryNode : LintNode
{
    internal BinaryNode(string op, LintNode left, LintNode right, int position)
        : base(position)
    {
        Op = op;
        Left = left;
        Right = right;
    }

    internal string Op { get; }

    internal LintNode Left { get; }

    internal LintNode Right { get; }

    internal override string Text => $"{Left.Text} {Op} {Right.Text}";
}

internal sealed class LetNode : LintNode
{
    internal LetNode(string name, LintNode value, LintNode body, int position)
        : base(position)
    {
        Name = name;
        Value = value;
        Body = body;
    }

    internal string Name { get; }

    internal LintNode Value { get; }

    internal LintNode Body { get; }

    internal override string Text => $"let {Name} = {Value.Text} in {Body.Text}";
}

internal sealed class ListNode : LintNode
{
    internal ListNode(List<LintNode> items, int position)
        : base(position)
    {
        Items = items;
    }

    internal List<LintNode> Items { get; }

    internal override string Text
    {
        get
        {
            StringBuilder text = new StringBuilder("[");
            for (int index = 0; index < Items.Count; index++)
            {
                text.Append(index > 0 ? ", " : string.Empty).Append(Items[index].Text);
            }

            return text.Append(']').ToString();
        }
    }
}

/// <summary>A parsed select: a set with an optional where, or pairs of one within a distance.</summary>
internal sealed class SelectNode
{
    internal SelectNode(string set, LintNode? where, double? within, int position)
    {
        Set = set;
        Where = where;
        Within = within;
        Position = position;
    }

    internal string Set { get; }

    internal LintNode? Where { get; }

    /// <summary>For pairs: the greatest gap between the two things' mesh boxes (positions without one), metres.</summary>
    internal double? Within { get; }

    internal bool IsPairs => Within.HasValue;

    internal int Position { get; }
}

/// <summary>
/// Recursive-descent parser. Precedence, loosest first: let, or, and, not, comparisons (== != &lt; &lt;= &gt; &gt;= in,
/// not in, matches, matches_regex), ??, + -, * / %, unary -, postfix (. ?. [] calls).
/// </summary>
internal sealed class LintParser
{
    private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        { "and", "or", "not", "in", "let", "true", "false", "null", "matches", "matches_regex", "where" };

    private readonly List<LintToken> _tokens;
    private int _at;

    // Inside a let's value, a bare 'in' ends the value (let k = 4 in k * k); brackets and calls lift that.
    private bool _letValue;

    private LintParser(string source)
    {
        _tokens = LintLexer.Tokens(source);
    }

    internal static LintNode Expression(string source)
    {
        LintParser parser = new LintParser(source);
        LintNode node = parser.ParseExpression();
        parser.Expect(TokenKind.End);
        return node;
    }

    internal static SelectNode Select(string source)
    {
        LintParser parser = new LintParser(source);
        SelectNode select = parser.ParseSelect();
        parser.Expect(TokenKind.End);
        return select;
    }

    private LintToken Peek => _tokens[_at];

    private LintToken PeekAt(int ahead) => _tokens[Math.Min(_at + ahead, _tokens.Count - 1)];

    private LintToken Next() => _tokens[_at++];

    private bool Accept(string symbol)
    {
        if (Peek.Is(symbol))
        {
            _at++;
            return true;
        }

        return false;
    }

    private LintToken Expect(string symbol)
    {
        if (!Peek.Is(symbol))
        {
            throw new LintSyntaxException($"expected '{symbol}' but found {Peek}", Peek.Position);
        }

        return Next();
    }

    private void Expect(TokenKind kind)
    {
        if (Peek.Kind != kind)
        {
            throw new LintSyntaxException(
                kind == TokenKind.End ? $"unexpected {Peek} after the expression" : $"expected a {kind} but found {Peek}",
                Peek.Position);
        }
    }

    private string ExpectName()
    {
        LintToken token = Peek;
        if (token.Kind != TokenKind.Name || Keywords.Contains(token.Text))
        {
            throw new LintSyntaxException($"expected a name but found {token}", token.Position);
        }

        _at++;
        return token.Text;
    }

    private LintNode Nested(Func<LintNode> parse)
    {
        bool outer = _letValue;
        _letValue = false;
        LintNode node = parse();
        _letValue = outer;
        return node;
    }

    private SelectNode ParseSelect()
    {
        LintToken start = Peek;
        if (start.Is("pairs") && PeekAt(1).Is("("))
        {
            _at += 2;
            SelectNode inner = ParseSelect();
            if (inner.IsPairs)
            {
                throw new LintSyntaxException("pairs of pairs is not a set", inner.Position);
            }

            Expect(",");
            Expect("within");
            Expect(":");
            LintToken distance = Peek;
            if (distance.Kind != TokenKind.Number || distance.Number < 0)
            {
                throw new LintSyntaxException("within needs a distance in metres, 0 or more", distance.Position);
            }

            _at++;
            Expect(")");
            return new SelectNode(inner.Set, inner.Where, distance.Number, start.Position);
        }

        string set = ExpectName();
        if (Array.IndexOf(LintSets.All, set) < 0)
        {
            throw new LintSyntaxException($"no set {set}; sets are {string.Join(", ", LintSets.All)}", start.Position);
        }

        LintNode? where = Accept("where") ? ParseExpression() : null;
        return new SelectNode(set, where, null, start.Position);
    }

    private LintNode ParseExpression()
    {
        if (Peek.Is("let"))
        {
            int position = Next().Position;
            string name = ExpectName();
            Expect("=");
            bool outer = _letValue;
            _letValue = true;
            LintNode value = ParseExpression();
            _letValue = outer;
            Expect("in");
            return new LetNode(name, value, ParseExpression(), position);
        }

        if (Peek.Kind == TokenKind.Name && PeekAt(1).Is("=>"))
        {
            LintToken parameter = Next();
            _at++;
            return new LambdaNode(parameter.Text, ParseExpression(), parameter.Position);
        }

        return ParseOr();
    }

    private LintNode ParseOr()
    {
        LintNode left = ParseAnd();
        while (Peek.Is("or"))
        {
            int position = Next().Position;
            left = new BinaryNode("or", left, ParseAnd(), position);
        }

        return left;
    }

    private LintNode ParseAnd()
    {
        LintNode left = ParseNot();
        while (Peek.Is("and"))
        {
            int position = Next().Position;
            left = new BinaryNode("and", left, ParseNot(), position);
        }

        return left;
    }

    private LintNode ParseNot()
    {
        if (Peek.Is("not"))
        {
            int position = Next().Position;
            return new UnaryNode("not", ParseNot(), position);
        }

        return ParseComparison();
    }

    private LintNode ParseComparison()
    {
        LintNode left = ParseCoalesce();
        LintToken op = Peek;
        if (op.Is("==") || op.Is("!=") || op.Is("<") || op.Is("<=") || op.Is(">") || op.Is(">=") ||
            (op.Is("in") && !_letValue) ||
            op.Is("matches") || op.Is("matches_regex"))
        {
            _at++;
            return new BinaryNode(op.Text, left, ParseCoalesce(), op.Position);
        }

        if (op.Is("not") && PeekAt(1).Is("in") && !_letValue)
        {
            _at += 2;
            return new UnaryNode("not", new BinaryNode("in", left, ParseCoalesce(), op.Position), op.Position);
        }

        return left;
    }

    private LintNode ParseCoalesce()
    {
        LintNode left = ParseAdditive();
        while (Peek.Is("??"))
        {
            int position = Next().Position;
            left = new BinaryNode("??", left, ParseAdditive(), position);
        }

        return left;
    }

    private LintNode ParseAdditive()
    {
        LintNode left = ParseMultiplicative();
        while (Peek.Is("+") || Peek.Is("-"))
        {
            LintToken op = Next();
            left = new BinaryNode(op.Text, left, ParseMultiplicative(), op.Position);
        }

        return left;
    }

    private LintNode ParseMultiplicative()
    {
        LintNode left = ParseUnary();
        while (Peek.Is("*") || Peek.Is("/") || Peek.Is("%"))
        {
            LintToken op = Next();
            left = new BinaryNode(op.Text, left, ParseUnary(), op.Position);
        }

        return left;
    }

    private LintNode ParseUnary()
    {
        if (Peek.Is("-"))
        {
            int position = Next().Position;
            return new UnaryNode("-", ParseUnary(), position);
        }

        return ParsePostfix(ParsePrimary());
    }

    private LintNode ParsePostfix(LintNode node)
    {
        while (true)
        {
            if (Peek.Is(".") || Peek.Is("?."))
            {
                bool nullSafe = Next().Text == "?.";
                LintToken name = Peek;
                string member = ExpectName();
                if (Peek.Is("("))
                {
                    (List<LintNode> args, Dictionary<string, LintNode> named) = ParseArguments();
                    args.Insert(0, node);
                    node = new CallNode(member, args, named, nullSafe, name.Position);
                }
                else
                {
                    node = new MemberNode(node, member, nullSafe, name.Position);
                }

                continue;
            }

            if (Peek.Is("["))
            {
                int position = Next().Position;
                LintNode index = Nested(ParseExpression);
                Expect("]");
                node = new IndexNode(node, index, position);
                continue;
            }

            return node;
        }
    }

    private LintNode ParsePrimary()
    {
        LintToken token = Peek;
        switch (token.Kind)
        {
            case TokenKind.Number:
                _at++;
                return new LiteralNode(LintValue.Of(token.Number), LintType.Number, token.Position);
            case TokenKind.String:
                _at++;
                return new LiteralNode(LintValue.Of(token.Text), LintType.String, token.Position);
            case TokenKind.Name:
                if (token.Is("true") || token.Is("false"))
                {
                    _at++;
                    return new LiteralNode(LintValue.Of(token.Text == "true"), LintType.Bool, token.Position);
                }

                if (token.Is("null"))
                {
                    _at++;
                    return new LiteralNode(LintValue.Null, LintType.Null, token.Position);
                }

                string name = ExpectName();
                if (Peek.Is("("))
                {
                    (List<LintNode> args, Dictionary<string, LintNode> named) = ParseArguments();
                    return new CallNode(name, args, named, false, token.Position);
                }

                return new NameNode(name, token.Position);
            case TokenKind.Symbol when token.Is("("):
                _at++;
                LintNode inner = Nested(ParseExpression);
                Expect(")");
                return inner;
            case TokenKind.Symbol when token.Is("["):
                _at++;
                List<LintNode> items = new List<LintNode>();
                if (!Peek.Is("]"))
                {
                    do
                    {
                        items.Add(Nested(ParseExpression));
                    }
                    while (Accept(","));
                }

                Expect("]");
                return new ListNode(items, token.Position);
            default:
                throw new LintSyntaxException($"expected a value but found {token}", token.Position);
        }
    }

    private (List<LintNode> Args, Dictionary<string, LintNode> Named) ParseArguments()
    {
        Expect("(");
        List<LintNode> args = new List<LintNode>();
        Dictionary<string, LintNode> named = new Dictionary<string, LintNode>(StringComparer.Ordinal);
        if (!Peek.Is(")"))
        {
            do
            {
                if (Peek.Kind == TokenKind.Name && PeekAt(1).Is(":"))
                {
                    LintToken key = Next();
                    _at++;
                    named[key.Text] = Nested(ParseExpression);
                }
                else
                {
                    args.Add(Nested(ParseExpression));
                }
            }
            while (Accept(","));
        }

        Expect(")");
        return (args, named);
    }
}
