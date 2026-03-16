public class Function
{
    public List<Instruction> Instructions = new();
    public List<object> Constants = new();
    public List<Function> Protos = new();
    public int NumParams;
    public int LuaVersion = 0x51;   
    public List<string> LocalNames = new();
    public List<string> UpvalueNames = new();
}