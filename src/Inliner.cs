using Loretta.CodeAnalysis;
using Loretta.CodeAnalysis.Lua;
using Loretta.CodeAnalysis.Lua.Syntax;
using System.Collections.Generic;
using System.Linq;
using static Loretta.CodeAnalysis.Lua.SyntaxFactory;

public static class Inliner
{
    public static string Process(string source)
    {
        var opts = new LuaParseOptions(LuaSyntaxOptions.Lua51);
        var tree = LuaSyntaxTree.ParseText(source, opts);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        root = InlineTables(root);
        root = HoistRepeatedExpressions(root);
        root = CleanupBracketAccess(root);
        root = RemoveEmptyIfBlocks(root);
        root = CollapseElseIfChains(root);

        return root.NormalizeWhitespace().ToFullString();
    }

    private static CompilationUnitSyntax InlineTables(CompilationUnitSyntax root)
    {
        string prev;
        do
        {
            prev = root.ToFullString();
            root = InlineTablesOnce(root);
        }
        while (root.ToFullString() != prev);
        return root;
    }

    private static CompilationUnitSyntax InlineTablesOnce(CompilationUnitSyntax root)
    {
        var statements = root.Statements.Statements.ToList();
        var result = new List<StatementSyntax>();
        int i = 0;

        while (i < statements.Count)
        {
            var stmt = statements[i];

            if (!IsEmptyTableAssignment(stmt, out string tableName))
            {
                result.Add(stmt);
                i++;
                continue;
            }

            var fields = new List<(string key, ExpressionSyntax value, bool isIdentKey)>();
            int j = i + 1;

            while (j < statements.Count
                && IsTableFieldAssignment(statements[j], tableName, out string key, out ExpressionSyntax value, out bool isIdentKey))
            {
                fields.Add((key, value, isIdentKey));
                j++;
            }

            if (!fields.Any())
            {
                result.Add(stmt);
                i++;
                continue;
            }

            bool inlinedIntoParent = false;

            if (j < statements.Count && IsAssignmentOf(statements[j], tableName))
            {
                var tableFields = new List<TableFieldSyntax>();
                foreach (var (key, value, isIdentKey) in fields)
                {
                    TableFieldSyntax field = isIdentKey
                        ? IdentifierKeyedTableField(Identifier(key), value)
                        : ExpressionKeyedTableField(
                            LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(key)),
                            value);
                    tableFields.Add(field);
                }

                var inlineTable = TableConstructorExpression(SeparatedList(tableFields));
                var rewriter = new SingleExpressionReplacer(tableName, inlineTable);
                var newAssign = (StatementSyntax)rewriter.Visit(statements[j]);
                result.Add(newAssign);

                i = j + 1;
                inlinedIntoParent = true;
            }

            if (!inlinedIntoParent)
            {
                result.Add(stmt);
                for (int k = i + 1; k < j; k++)
                    result.Add(statements[k]);
                i = j;
            }
        }

        return root.WithStatements(StatementList(List(result)));
    }

    private static bool IsEmptyTableAssignment(StatementSyntax stmt, out string name)
    {
        name = null;
        if (stmt is not AssignmentStatementSyntax assign) return false;
        if (assign.Variables.Count != 1) return false;
        if (assign.EqualsValues.Values.Count != 1) return false;
        if (assign.Variables[0] is not IdentifierNameSyntax id) return false;
        if (assign.EqualsValues.Values[0] is not TableConstructorExpressionSyntax table) return false;
        if (table.Fields.Any()) return false;
        name = id.Name;
        return true;
    }

    private static bool IsTableFieldAssignment(StatementSyntax stmt, string tableName,
        out string key, out ExpressionSyntax value, out bool isIdentKey)
    {
        key = null; value = null; isIdentKey = false;
        if (stmt is not AssignmentStatementSyntax assign) return false;
        if (assign.Variables.Count != 1) return false;
        if (assign.EqualsValues.Values.Count != 1) return false;

        var target = assign.Variables[0];

        if (target is ElementAccessExpressionSyntax elemAccess
            && elemAccess.Expression is IdentifierNameSyntax elemId
            && elemId.Name == tableName
            && elemAccess.KeyExpression is LiteralExpressionSyntax lit
            && lit.IsKind(SyntaxKind.StringLiteralExpression))
        {
            key = lit.Token.ValueText;
            value = assign.EqualsValues.Values[0];
            isIdentKey = System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Za-z_][A-Za-z0-9_]*$");
            return true;
        }

        if (target is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Expression is IdentifierNameSyntax memberId
            && memberId.Name == tableName)
        {
            key = memberAccess.MemberName.Text;
            value = assign.EqualsValues.Values[0];
            isIdentKey = true;
            return true;
        }

        return false;
    }

    private static bool IsAssignmentOf(StatementSyntax stmt, string tableName)
    {
        if (stmt is not AssignmentStatementSyntax assign) return false;
        if (assign.EqualsValues.Values.Count != 1) return false;
        if (assign.EqualsValues.Values[0] is not IdentifierNameSyntax id) return false;
        return id.Name == tableName;
    }

    private static CompilationUnitSyntax HoistRepeatedExpressions(CompilationUnitSyntax root)
    {
        int globalIdx = 0;

        for (int round = 0; round < 10; round++)
        {
            var groups = root.DescendantNodes()
                .OfType<ExpressionSyntax>()
                .Where(IsHoistCandidate)
                .GroupBy(e => e.ToFullString().Trim())
                .Where(g => g.Count() >= 2)
                .OrderByDescending(g => g.Key.Length)
                .ToList();

            if (!groups.Any()) break;

            var hoisted = new List<StatementSyntax>();
            var replacements = new Dictionary<string, ExpressionSyntax>();

            foreach (var group in groups)
            {
                string key = group.Key;
                if (replacements.ContainsKey(key)) continue;

                string varName = $"_h{globalIdx++}";
                replacements[key] = IdentifierName(varName);

                hoisted.Add(AssignmentStatement(
                    SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(varName) }),
                    EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { group.First() }))));
            }

            root = (CompilationUnitSyntax)new ExpressionReplacer(replacements).Visit(root);
            root = root.WithStatements(
                StatementList(List(hoisted.Concat(root.Statements.Statements))));
        }

        return root;
    }

    private static bool IsHoistCandidate(ExpressionSyntax expr)
    {
        if (expr is not FunctionCallExpressionSyntax
            && expr is not MethodCallExpressionSyntax)
            return false;

        string text = expr.ToFullString().Trim();
        if (text.Length < 15) return false;

        if (expr.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>().Any())
            return false;

        if (expr is FunctionCallExpressionSyntax fc
            && fc.Expression is IdentifierNameSyntax)
            return false;

        return true;
    }


    private static CompilationUnitSyntax CleanupBracketAccess(CompilationUnitSyntax root)
        => (CompilationUnitSyntax)new BracketAccessRewriter().Visit(root);


    private static CompilationUnitSyntax RemoveEmptyIfBlocks(CompilationUnitSyntax root)
    {
        string prev;
        do
        {
            prev = root.ToFullString();
            root = (CompilationUnitSyntax)new EmptyIfRewriter().Visit(root);
        }
        while (root.ToFullString() != prev);
        return root;
    }


    private static CompilationUnitSyntax CollapseElseIfChains(CompilationUnitSyntax root)
    {
        string prev;
        do
        {
            prev = root.ToFullString();
            root = (CompilationUnitSyntax)new ElseIfCollapser().Visit(root);
        }
        while (root.ToFullString() != prev);
        return root;
    }


    private class SingleExpressionReplacer : LuaSyntaxRewriter
    {
        private readonly string _name;
        private readonly ExpressionSyntax _replacement;

        public SingleExpressionReplacer(string name, ExpressionSyntax replacement)
        {
            _name = name;
            _replacement = replacement;
        }

        public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            => node.Name == _name ? _replacement : node;
    }

    private class ExpressionReplacer : LuaSyntaxRewriter
    {
        private readonly Dictionary<string, ExpressionSyntax> _map;
        public ExpressionReplacer(Dictionary<string, ExpressionSyntax> map) => _map = map;

        public override SyntaxNode VisitFunctionCallExpression(FunctionCallExpressionSyntax node)
        {
            node = (FunctionCallExpressionSyntax)base.VisitFunctionCallExpression(node);
            string key = node.ToFullString().Trim();
            return _map.TryGetValue(key, out var r) ? r : node;
        }

        public override SyntaxNode VisitMethodCallExpression(MethodCallExpressionSyntax node)
        {
            node = (MethodCallExpressionSyntax)base.VisitMethodCallExpression(node);
            string key = node.ToFullString().Trim();
            return _map.TryGetValue(key, out var r) ? r : node;
        }
    }

    private class BracketAccessRewriter : LuaSyntaxRewriter
    {
        private static readonly System.Text.RegularExpressions.Regex ValidIdent =
            new(@"^[A-Za-z_][A-Za-z0-9_]*$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public override SyntaxNode VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        {
            node = (ElementAccessExpressionSyntax)base.VisitElementAccessExpression(node);

            if (node.KeyExpression is LiteralExpressionSyntax lit
                && lit.IsKind(SyntaxKind.StringLiteralExpression)
                && ValidIdent.IsMatch(lit.Token.ValueText))
            {
                var prefix = node.Expression as PrefixExpressionSyntax
                             ?? ParenthesizedExpression(node.Expression);
                return MemberAccessExpression(
                    prefix,
                    Token(SyntaxKind.DotToken),
                    Identifier(lit.Token.ValueText));
            }

            return node;
        }
    }

    private class EmptyIfRewriter : LuaSyntaxRewriter
    {
        public override SyntaxNode VisitIfStatement(IfStatementSyntax node)
        {
            node = (IfStatementSyntax)base.VisitIfStatement(node);

            bool thenEmpty = !node.Body.Statements.Any();
            bool noElseIf = !node.ElseIfClauses.Any();
            bool hasElse = node.ElseClause != null;
            bool elseEmpty = hasElse && !node.ElseClause.ElseBody.Statements.Any();

            if (thenEmpty && noElseIf && !hasElse)
                return null;

            if (thenEmpty && noElseIf && hasElse && !elseEmpty)
            {
                var flipped = FlipCondition(node.Condition);
                return IfStatement(flipped, node.ElseClause.ElseBody);
            }

            if (!thenEmpty && hasElse && elseEmpty)
                return node.WithElseClause(null);

            if (thenEmpty && noElseIf && hasElse && elseEmpty)
                return null;

            return node;
        }

        private static ExpressionSyntax FlipCondition(ExpressionSyntax cond)
        {
            if (cond is UnaryExpressionSyntax u && u.IsKind(SyntaxKind.LogicalNotExpression))
                return u.Operand;

            return UnaryExpression(
                SyntaxKind.LogicalNotExpression,
                Token(SyntaxKind.NotKeyword),
                ParenthesizedExpression(cond));
        }
    }

    private class ElseIfCollapser : LuaSyntaxRewriter
    {
        public override SyntaxNode VisitIfStatement(IfStatementSyntax node)
        {
            node = (IfStatementSyntax)base.VisitIfStatement(node);

            while (node.ElseClause != null
                && node.ElseClause.ElseBody.Statements.Count == 1
                && node.ElseClause.ElseBody.Statements[0] is IfStatementSyntax inner)
            {
                var newElseIf = ElseIfClause(inner.Condition, inner.Body);

                node = node
                    .WithElseIfClauses(
                        node.ElseIfClauses
                            .AddRange(new[] { newElseIf })
                            .AddRange(inner.ElseIfClauses))
                    .WithElseClause(inner.ElseClause);
            }

            return node;
        }
    }
}