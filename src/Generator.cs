using Loretta.CodeAnalysis;
using Loretta.CodeAnalysis.Lua;
using Loretta.CodeAnalysis.Lua.Syntax;
using System.Collections.Generic;
using System.Linq;
using static Loretta.CodeAnalysis.Lua.SyntaxFactory;

public class Generator
{
    private RegisterState regs = new();
    private Function currentFunc;

    public string Generate(List<Instruction> instructions, List<object> constants, Function func)
    {
        currentFunc = func;

        for (int i = 0; i < func.LocalNames.Count; i++)
        {
            string name = func.LocalNames[i];
            if (name != null && !name.StartsWith("(for "))
                regs.SetName(i, name);
        }

        int pc = 0;
        var statements = CollectBody(instructions, constants, ref pc, instructions.Count);

        var unit = CompilationUnit(
            StatementList(List(statements)),
            Token(SyntaxKind.EndOfFileToken));

        string raw = unit.NormalizeWhitespace().ToFullString();

        var opts2 = new LuaParseOptions(LuaSyntaxOptions.Lua51);
        var tree2 = LuaSyntaxTree.ParseText(raw, opts2);
        return tree2.GetRoot().NormalizeWhitespace().ToFullString();
    }

    private List<StatementSyntax> CollectBody(List<Instruction> instructions, List<object> constants, ref int pc, int endPc)
    {
        var body = new List<StatementSyntax>();

        while (pc < instructions.Count && pc < endPc)
        {
            var inst = instructions[pc];
            Console.WriteLine($"PC:{pc} opcode='{inst.Opcode}'");

            if (inst.Opcode == "FORPREP")
            {
                var s = HandleFORPREPFULL(instructions, constants, ref pc);
                if (s != null) body.Add(s);
                pc++;
                continue;
            }

            if (inst.Opcode == "NEWTABLE")
            {
                string localName = inst.A < currentFunc.LocalNames.Count ? currentFunc.LocalNames[inst.A] : null;
                if (localName != null && localName.StartsWith("(for ")) localName = null;

                var tableExpr = TableConstructorExpression(SeparatedList<TableFieldSyntax>());

                if (localName != null)
                {
                    regs.Set(inst.A, IdentifierName(localName));
                    regs.SetName(inst.A, localName);
                    body.Add(AssignmentStatement(
                        SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(localName) }),
                        EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { tableExpr }))));
                    pc++;
                    continue;
                }

                if (pc + 1 < instructions.Count && instructions[pc + 1].Opcode == "SETGLOBAL")
                {
                    var nextInst = instructions[pc + 1];
                    string tname = (string)constants[nextInst.Bx];
                    regs.Set(inst.A, IdentifierName(tname));
                    regs.SetName(inst.A, tname);
                    body.Add(AssignmentStatement(
                        SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(tname) }),
                        EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { tableExpr }))));
                    pc += 2;
                    continue;
                }

                string tempName = $"t{inst.A}";
                regs.Set(inst.A, IdentifierName(tempName));
                regs.SetName(inst.A, tempName);
                body.Add(AssignmentStatement(
                    SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(tempName) }),
                    EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { tableExpr }))));
                pc++;
                continue;
            }

            if (inst.Opcode == "EQ" || inst.Opcode == "LT" || inst.Opcode == "LE")
            {
                var s = HandleCompareJMP(instructions, constants, ref pc);
                if (s != null) body.Add(s);
                pc++;
                continue;
            }

            if (inst.Opcode == "TEST")
            {
                var s = HandleTESTJMP(instructions, constants, ref pc);
                if (s != null) body.Add(s);
                pc++;
                continue;
            }

            if (inst.Opcode == "TESTSET")
            {
                var s = HandleTESTSETJMP(instructions, constants, ref pc);
                if (s != null) body.Add(s);
                pc++;
                continue;
            }

            StatementSyntax stmt = inst.Opcode switch
            {
                "GETGLOBAL" => HandleGETGLOBAL(inst, constants),
                "LOADK" => HandleLOADK(inst, constants),
                "LOADBOOL" => HandleLOADBOOL(inst),
                "MOVE" => HandleMOVE(inst),
                "CALL" => HandleCALL(inst),
                "RETURN" => HandleRETURN(inst),
                "ADD" => HandleADD(inst),
                "SUB" => HandleSUB(inst),
                "MUL" => HandleMUL(inst),
                "SETGLOBAL" => HandleSETGLOBAL(inst, constants),
                "DIV" => HandleDIV(inst),
                "MOD" => HandleMOD(inst),
                "POW" => HandlePOW(inst),
                "UNM" => HandleUNM(inst),
                "NOT" => HandleNOT(inst),
                "LEN" => HandleLEN(inst),
                "CONCAT" => HandleCONCAT(inst),
                "GETTABLE" => HandleGETTABLE(inst, constants),
                "SETTABLE" => HandleSETTABLE(inst, constants),
                "CLOSURE" => HandleCLOSURE(inst),
                "LOADNIL" => HandleLOADNIL(inst),
                "GETUPVAL" => HandleGETUPVAL(inst),
                "SETUPVAL" => HandleSETUPVAL(inst),
                "TAILCALL" => HandleCALL(inst),
                "VARARG" => HandleVARARG(inst),
                "SELF" => HandleSELF(inst, constants),
                "SETLIST" => HandleSETLIST(inst),
                "FORLOOP" => null,
                "JMP" => null,
                _ => null
            };

            if (stmt != null) body.Add(stmt);
            pc++;
        }

        return body;
    }

    private StatementSyntax HandleFORPREPFULL(List<Instruction> instructions, List<object> constants, ref int pc)
    {
        var inst = instructions[pc];
        var start = regs.Get(inst.A);
        var limit = regs.Get(inst.A + 1);
        var step = regs.Get(inst.A + 2);

        regs.Clear(inst.A);
        regs.Clear(inst.A + 1);
        regs.Clear(inst.A + 2);
        regs.Clear(inst.A + 3);

        string varName = (inst.A + 3) < currentFunc.LocalNames.Count
            ? currentFunc.LocalNames[inst.A + 3]
            : $"i{inst.A}";

        if (varName.StartsWith("(for "))
            varName = $"i{inst.A}";

        regs.Set(inst.A + 3, IdentifierName(varName));

        pc++;
        int bodyStart = pc;
        int bodyEnd = pc;
        while (bodyEnd < instructions.Count && instructions[bodyEnd].Opcode != "FORLOOP")
            bodyEnd++;

        var bodyStatements = CollectBody(instructions, constants, ref pc, bodyEnd);

        // skip forloop
        if (pc < instructions.Count && instructions[pc].Opcode == "FORLOOP")
            pc++;
        pc--; 

        return NumericForStatement(
            varName,
            start,
            limit,
            step,
            StatementList(List(bodyStatements)));
    }

    private StatementSyntax HandleCompareJMP(List<Instruction> instructions, List<object> constants, ref int pc)
    {
        var cmpInst = instructions[pc];
        var left = GetRK(cmpInst.B, constants);
        var right = GetRK(cmpInst.C, constants);

        pc++;
        if (pc >= instructions.Count || instructions[pc].Opcode != "JMP")
            return null;

        var jmpInst = instructions[pc];
        int offset = jmpInst.sBx;
        int bodyEnd = pc + 1 + offset;

        bool negate = cmpInst.A != 0;

        ExpressionSyntax condition = cmpInst.Opcode switch
        {
            "EQ" => BinaryExpression(
                        negate ? SyntaxKind.NotEqualsExpression : SyntaxKind.EqualsExpression,
                        left,
                        Token(negate ? SyntaxKind.TildeEqualsToken : SyntaxKind.EqualsEqualsToken),
                        right),
            "LT" => negate
                        ? BinaryExpression(SyntaxKind.GreaterThanOrEqualExpression, left, Token(SyntaxKind.GreaterThanEqualsToken), right)
                        : BinaryExpression(SyntaxKind.LessThanExpression, left, Token(SyntaxKind.LessThanToken), right),
            "LE" => negate
                        ? BinaryExpression(SyntaxKind.GreaterThanExpression, left, Token(SyntaxKind.GreaterThanToken), right)
                        : BinaryExpression(SyntaxKind.LessThanOrEqualExpression, left, Token(SyntaxKind.LessThanEqualsToken), right),
            _ => BinaryExpression(SyntaxKind.EqualsExpression, left, Token(SyntaxKind.EqualsEqualsToken), right)
        };

        pc++;
        int thenEnd = bodyEnd;
        int elseEnd = -1;

        if (bodyEnd - 1 >= 0 && bodyEnd - 1 < instructions.Count
            && instructions[bodyEnd - 1].Opcode == "JMP")
        {
            var elseJmp = instructions[bodyEnd - 1];
            elseEnd = bodyEnd + elseJmp.sBx;
            thenEnd = bodyEnd - 1;
        }

        var thenStatements = CollectBody(instructions, constants, ref pc, thenEnd);

        if (elseEnd > 0)
        {
            pc++;
            var elseStatements = CollectBody(instructions, constants, ref pc, elseEnd);
            pc--;
            return IfStatement(condition, StatementList(List(thenStatements)))
                .WithElseClause(ElseClause(StatementList(List(elseStatements))));
        }

        pc--;
        return IfStatement(condition, StatementList(List(thenStatements)));
    }

    private StatementSyntax HandleTESTJMP(List<Instruction> instructions, List<object> constants, ref int pc)
    {
        var testInst = instructions[pc];
        var val = regs.Get(testInst.A);

        pc++;
        if (pc >= instructions.Count || instructions[pc].Opcode != "JMP")
            return null;

        var jmpInst = instructions[pc];
        int offset = jmpInst.sBx;
        int bodyEnd = pc + 1 + offset;

        ExpressionSyntax condition = testInst.C == 0
            ? (ExpressionSyntax)UnaryExpression(SyntaxKind.LogicalNotExpression, Token(SyntaxKind.NotKeyword), val)
            : val;

        pc++;
        int thenEnd = bodyEnd;
        int elseEnd = -1;

        if (bodyEnd - 1 >= 0 && bodyEnd - 1 < instructions.Count
            && instructions[bodyEnd - 1].Opcode == "JMP")
        {
            var elseJmp = instructions[bodyEnd - 1];
            elseEnd = bodyEnd + elseJmp.sBx;
            thenEnd = bodyEnd - 1;
        }

        var thenStatements = CollectBody(instructions, constants, ref pc, thenEnd);

        if (elseEnd > 0)
        {
            pc++;
            var elseStatements = CollectBody(instructions, constants, ref pc, elseEnd);
            pc--;
            return IfStatement(condition, StatementList(List(thenStatements)))
                .WithElseClause(ElseClause(StatementList(List(elseStatements))));
        }

        pc--;
        return IfStatement(condition, StatementList(List(thenStatements)));
    }

    private StatementSyntax HandleTESTSETJMP(List<Instruction> instructions, List<object> constants, ref int pc)
    {
        var tsInst = instructions[pc];
        var lhs = regs.Get(tsInst.A);
        var rhs = regs.Get(tsInst.B);

        pc++;
        if (pc < instructions.Count && instructions[pc].Opcode == "JMP")
            pc++;

        ExpressionSyntax expr = tsInst.C == 0
            ? (ExpressionSyntax)BinaryExpression(SyntaxKind.LogicalAndExpression, lhs, Token(SyntaxKind.AndKeyword), rhs)
            : BinaryExpression(SyntaxKind.LogicalOrExpression, lhs, Token(SyntaxKind.OrKeyword), rhs);

        regs.Set(tsInst.A, expr);

        string name = regs.GetName(tsInst.A);
        if (name != null)
        {
            return AssignmentStatement(
                SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(name) }),
                EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { expr })));
        }

        pc--;
        return null;
    }

    private StatementSyntax HandleGETGLOBAL(Instruction inst, List<object> constants)
    {
        string name = (string)constants[inst.Bx];
        regs.Set(inst.A, IdentifierName(name));
        regs.SetName(inst.A, name);
        return null;
    }

    private StatementSyntax HandleSETGLOBAL(Instruction inst, List<object> constants)
    {
        string name = (string)constants[inst.Bx];
        var value = regs.Get(inst.A);
        var target = IdentifierName(name);
        regs.Set(inst.A, IdentifierName(name));
        regs.SetName(inst.A, name);
        return AssignmentStatement(
            SeparatedList<PrefixExpressionSyntax>(new[] { target }),
            EqualsValuesClause(SeparatedList(new[] { value })));
    }

    private StatementSyntax HandleLOADK(Instruction inst, List<object> constants)
    {
        var expr = ConstantToExpression(constants[inst.Bx]);

        string localName = inst.A < currentFunc.LocalNames.Count
            ? currentFunc.LocalNames[inst.A]
            : null;

        if (localName != null && !localName.StartsWith("(for "))
        {
            regs.Set(inst.A, IdentifierName(localName));
            return AssignmentStatement(
                SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(localName) }),
                EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { expr })));
        }

        regs.Set(inst.A, expr);
        return null;
    }

    private StatementSyntax HandleLOADBOOL(Instruction inst)
    {
        var expr = LiteralExpression(inst.B != 0
            ? SyntaxKind.TrueLiteralExpression
            : SyntaxKind.FalseLiteralExpression);

        string localName = inst.A < currentFunc.LocalNames.Count ? currentFunc.LocalNames[inst.A] : null;
        bool isFirstWrite = !regs.Has(inst.A);

        if (localName != null && !localName.StartsWith("(for ") && isFirstWrite)
        {
            regs.Set(inst.A, IdentifierName(localName));
            return AssignmentStatement(
                SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(localName) }),
                EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { expr })));
        }

        regs.Set(inst.A, expr);
        return null;
    }

    private StatementSyntax HandleLOADNIL(Instruction inst)
    {
        var nil = LiteralExpression(SyntaxKind.NilLiteralExpression);
        for (int r = inst.A; r <= inst.B; r++)
            regs.Set(r, nil);
        return null;
    }

    private StatementSyntax HandleMOVE(Instruction inst)
    {
        regs.Set(inst.A, regs.Get(inst.B));
        regs.SetName(inst.A, regs.GetName(inst.B));
        return null;
    }

    private StatementSyntax HandleGETUPVAL(Instruction inst)
    {
        string name = inst.B < currentFunc.UpvalueNames.Count
            ? currentFunc.UpvalueNames[inst.B]
            : $"upval{inst.B}";
        regs.Set(inst.A, IdentifierName(name));
        regs.SetName(inst.A, name);
        return null;
    }

    private StatementSyntax HandleSETUPVAL(Instruction inst)
    {
        string name = inst.B < currentFunc.UpvalueNames.Count
            ? currentFunc.UpvalueNames[inst.B]
            : $"upval{inst.B}";
        var value = regs.Get(inst.A);
        var target = IdentifierName(name);
        return AssignmentStatement(
            SeparatedList<PrefixExpressionSyntax>(new[] { target }),
            EqualsValuesClause(SeparatedList(new[] { value })));
    }

    private StatementSyntax HandleSELF(Instruction inst, List<object> constants)
    {
        var obj = regs.Get(inst.B);
        string key = (string)constants[inst.C & 0xFF];
        var prefix = obj as PrefixExpressionSyntax ?? ParenthesizedExpression(obj);
        regs.Set(inst.A + 1, obj);
        regs.Set(inst.A, MemberAccessExpression(prefix, Token(SyntaxKind.DotToken), Identifier(key)));
        return null;
    }

    private StatementSyntax HandleSETLIST(Instruction inst)
    {
        var fields = new List<TableFieldSyntax>();
        for (int i = 1; i <= inst.B; i++)
            fields.Add(UnkeyedTableField(regs.Get(inst.A + i)));

        var table = regs.Get(inst.A);
        if (table is TableConstructorExpressionSyntax t)
        {
            var newTable = TableConstructorExpression(t.Fields.AddRange(fields));
            regs.Set(inst.A, newTable);

            string name = regs.GetName(inst.A);
            if (name != null)
            {
                return AssignmentStatement(
                    SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(name) }),
                    EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { newTable })));
            }
        }
        return null;
    }

    private StatementSyntax HandleVARARG(Instruction inst)
    {
        var vararg = VarArgExpression();
        if (inst.B == 0)
            regs.Set(inst.A, vararg);
        else
            for (int i = 0; i < inst.B - 1; i++)
                regs.Set(inst.A + i, vararg);
        return null;
    }

    private StatementSyntax HandleCLOSURE(Instruction inst)
    {
        var proto = currentFunc.Protos[inst.Bx];

        var paramTokens = new List<SyntaxToken>();
        for (int i = 0; i < proto.NumParams; i++)
        {
            string name = i < proto.LocalNames.Count ? proto.LocalNames[i] : $"p{i}";
            paramTokens.Add(Identifier(name));
        }

        var paramList = ParameterList(
            SeparatedList(paramTokens.ConvertAll(p => (ParameterSyntax)NamedParameter(p))));

        var childGen = new Generator();
        for (int i = 0; i < proto.NumParams; i++)
        {
            string name = i < proto.LocalNames.Count ? proto.LocalNames[i] : $"p{i}";
            childGen.SeedRegister(i, IdentifierName(name));
        }

        string bodySource = childGen.Generate(proto.Instructions, proto.Constants, proto);
        var parseOpts = new LuaParseOptions(LuaSyntaxOptions.Lua51);
        var bodyTree = LuaSyntaxTree.ParseText(bodySource, parseOpts);
        var bodyStmts = ((CompilationUnitSyntax)bodyTree.GetRoot()).Statements;
        var anonFunc = AnonymousFunctionExpression(paramList, bodyStmts);

        string localName = inst.A < currentFunc.LocalNames.Count ? currentFunc.LocalNames[inst.A] : null;
        if (localName != null)
        {
            regs.Set(inst.A, IdentifierName(localName));
            return AssignmentStatement(
                SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(localName) }),
                EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { anonFunc })));
        }

        regs.Set(inst.A, anonFunc);
        return null;
    }

    private StatementSyntax HandleGETTABLE(Instruction inst, List<object> constants)
    {
        var table = regs.Get(inst.B);
        var key = GetRK(inst.C, constants);
        var prefix = table as PrefixExpressionSyntax ?? ParenthesizedExpression(table);
        regs.Set(inst.A, ElementAccessExpression(prefix, key));
        return null;
    }

    private StatementSyntax HandleSETTABLE(Instruction inst, List<object> constants)
    {
        var table = regs.Get(inst.A);
        var key = GetRK(inst.B, constants);
        var value = GetRK(inst.C, constants);
        string tname = regs.GetName(inst.A);
        var prefix = tname != null
            ? (PrefixExpressionSyntax)IdentifierName(tname)
            : table as PrefixExpressionSyntax ?? ParenthesizedExpression(table);

        return AssignmentStatement(
            SeparatedList<PrefixExpressionSyntax>(new[] { ElementAccessExpression(prefix, key) }),
            EqualsValuesClause(SeparatedList(new[] { value })));
    }

    private StatementSyntax HandleADD(Instruction inst)
    {
        var expr = BinaryExpression(SyntaxKind.AddExpression, regs.Get(inst.B), Token(SyntaxKind.PlusToken), regs.Get(inst.C));
        string name = regs.GetName(inst.A);
        if (name != null) { regs.Set(inst.A, IdentifierName(name)); return AssignmentStatement(SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(name) }), EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { expr }))); }
        regs.Set(inst.A, expr); return null;
    }

    private StatementSyntax HandleSUB(Instruction inst)
    {
        var expr = BinaryExpression(SyntaxKind.SubtractExpression, regs.Get(inst.B), Token(SyntaxKind.MinusToken), regs.Get(inst.C));
        string name = regs.GetName(inst.A);
        if (name != null) { regs.Set(inst.A, IdentifierName(name)); return AssignmentStatement(SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(name) }), EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { expr }))); }
        regs.Set(inst.A, expr); return null;
    }

    private StatementSyntax HandleMUL(Instruction inst)
    {
        var expr = BinaryExpression(SyntaxKind.MultiplyExpression, regs.Get(inst.B), Token(SyntaxKind.StarToken), regs.Get(inst.C));
        string name = regs.GetName(inst.A);
        if (name != null) { regs.Set(inst.A, IdentifierName(name)); return AssignmentStatement(SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(name) }), EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { expr }))); }
        regs.Set(inst.A, expr); return null;
    }

    private StatementSyntax HandleDIV(Instruction inst)
    {
        var expr = BinaryExpression(SyntaxKind.DivideExpression, regs.Get(inst.B), Token(SyntaxKind.SlashToken), regs.Get(inst.C));
        string name = regs.GetName(inst.A);
        if (name != null) { regs.Set(inst.A, IdentifierName(name)); return AssignmentStatement(SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(name) }), EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { expr }))); }
        regs.Set(inst.A, expr); return null;
    }

    private StatementSyntax HandleMOD(Instruction inst)
    {
        var expr = BinaryExpression(SyntaxKind.ModuloExpression, regs.Get(inst.B), Token(SyntaxKind.PercentToken), regs.Get(inst.C));
        string name = regs.GetName(inst.A);
        if (name != null) { regs.Set(inst.A, IdentifierName(name)); return AssignmentStatement(SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(name) }), EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { expr }))); }
        regs.Set(inst.A, expr); return null;
    }

    private StatementSyntax HandlePOW(Instruction inst)
    {
        var expr = BinaryExpression(SyntaxKind.ExponentiateExpression, regs.Get(inst.B), Token(SyntaxKind.HatToken), regs.Get(inst.C));
        string name = regs.GetName(inst.A);
        if (name != null) { regs.Set(inst.A, IdentifierName(name)); return AssignmentStatement(SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(name) }), EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { expr }))); }
        regs.Set(inst.A, expr); return null;
    }

    private StatementSyntax HandleUNM(Instruction inst)
    {
        var expr = UnaryExpression(SyntaxKind.UnaryMinusExpression, Token(SyntaxKind.MinusToken), regs.Get(inst.B));
        string name = regs.GetName(inst.A);
        if (name != null) { regs.Set(inst.A, IdentifierName(name)); return AssignmentStatement(SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(name) }), EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { expr }))); }
        regs.Set(inst.A, expr); return null;
    }

    private StatementSyntax HandleNOT(Instruction inst)
    {
        var expr = UnaryExpression(SyntaxKind.LogicalNotExpression, Token(SyntaxKind.NotKeyword), regs.Get(inst.B));
        string name = regs.GetName(inst.A);
        if (name != null) { regs.Set(inst.A, IdentifierName(name)); return AssignmentStatement(SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(name) }), EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { expr }))); }
        regs.Set(inst.A, expr); return null;
    }

    private StatementSyntax HandleLEN(Instruction inst)
    {
        var expr = UnaryExpression(SyntaxKind.LengthExpression, Token(SyntaxKind.HashToken), regs.Get(inst.B));
        string name = regs.GetName(inst.A);
        if (name != null) { regs.Set(inst.A, IdentifierName(name)); return AssignmentStatement(SeparatedList<PrefixExpressionSyntax>(new[] { IdentifierName(name) }), EqualsValuesClause(SeparatedList<ExpressionSyntax>(new[] { expr }))); }
        regs.Set(inst.A, expr); return null;
    }

    private StatementSyntax HandleCONCAT(Instruction inst)
    {
        var expr = regs.Get(inst.B);
        for (int i = inst.B + 1; i <= inst.C; i++)
            expr = BinaryExpression(SyntaxKind.ConcatExpression, expr, Token(SyntaxKind.DotDotToken), regs.Get(i));
        regs.Set(inst.A, expr);
        return null;
    }

    private StatementSyntax HandleCALL(Instruction inst)
    {
        var func = regs.Get(inst.A);
        var args = new List<ExpressionSyntax>();

        if (inst.B == 0)
        {
            int i = inst.A + 1;
            while (regs.Has(i)) { args.Add(regs.Get(i)); regs.Clear(i); i++; }
        }
        else
        {
            for (int i = 1; i < inst.B; i++)
                args.Add(regs.Get(inst.A + i));
        }

        var prefix = func as PrefixExpressionSyntax ?? ParenthesizedExpression(func);
        var call = FunctionCallExpression(prefix, ExpressionListFunctionArgument(SeparatedList(args)));

        if (inst.C == 0)
        {
            int r = inst.A;
            while (regs.Has(r)) { regs.Clear(r); r++; }
            regs.Set(inst.A, call);
            return null;
        }

        if (inst.C > 1)
        {
            for (int i = 0; i < inst.C - 1; i++)
                regs.Set(inst.A + i, call);
            return null;
        }

        return ExpressionStatement(call);
    }

    private StatementSyntax HandleRETURN(Instruction inst)
    {
        if (inst.B <= 1)
            return null;

        var values = new List<ExpressionSyntax>();
        for (int i = 0; i < inst.B - 1; i++)
            values.Add(regs.Get(inst.A + i));

        return ReturnStatement(SeparatedList(values));
    }

    public void SeedRegister(int reg, ExpressionSyntax expr)
    {
        regs.Set(reg, expr);
    }

    private ExpressionSyntax GetRK(int field, List<object> constants)
    {
        if ((field & 0x100) != 0)
            return ConstantToExpression(constants[field & 0xFF]);
        return regs.Get(field);
    }

    private static ExpressionSyntax ConstantToExpression(object constant)
    {
        return constant switch
        {
            string s => LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(s)),
            double d => LiteralExpression(SyntaxKind.NumericalLiteralExpression, Literal(d)),
            bool b => LiteralExpression(b ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
            null => LiteralExpression(SyntaxKind.NilLiteralExpression),
            _ => IdentifierName(constant.ToString())
        };
    }
}