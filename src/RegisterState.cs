using Loretta.CodeAnalysis.Lua;
using Loretta.CodeAnalysis.Lua.Syntax;
using System.Collections.Generic;

public class RegisterState
{
    private Dictionary<int, ExpressionSyntax> expressions = new();
    private Dictionary<int, string> names = new();
    private HashSet<int> declared = new();
    public bool IsDeclared(int reg) => declared.Contains(reg);
    public void Declare(int reg) => declared.Add(reg);

    public void Set(int reg, ExpressionSyntax expr) => expressions[reg] = expr;
    public void SetName(int reg, string name) => names[reg] = name;

    public ExpressionSyntax Get(int reg) =>
        expressions.TryGetValue(reg, out var e) ? e : SyntaxFactory.IdentifierName($"v{reg}");

    public string GetName(int reg) =>
        names.TryGetValue(reg, out var n) ? n : $"v{reg}";

    public bool Has(int reg) =>
        expressions.ContainsKey(reg) || names.ContainsKey(reg);

    public void Clear(int reg)
    {
        expressions.Remove(reg);
        names.Remove(reg);
    }
}