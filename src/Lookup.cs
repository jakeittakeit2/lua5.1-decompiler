public static class Lookup
{
    static string[] names =
    {
        "MOVE","LOADK","LOADBOOL","LOADNIL","GETUPVAL","GETGLOBAL",
        "GETTABLE","SETGLOBAL","SETUPVAL","SETTABLE","NEWTABLE",
        "SELF","ADD","SUB","MUL","DIV","MOD","POW","UNM","NOT",
        "LEN","CONCAT","JMP","EQ","LT","LE","TEST","TESTSET",
        "CALL","TAILCALL","RETURN","FORLOOP","FORPREP","TFORLOOP",
        "SETLIST","CLOSE","CLOSURE","VARARG"
    };

    public static string GetName(int opcode)
    {
        return names[opcode].Trim();
    }
}