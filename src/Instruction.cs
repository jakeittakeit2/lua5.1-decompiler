public class Instruction
{
    public string Opcode { get; set; }
    public int A { get; set; }
    public int B { get; set; }
    public int C { get; set; }
    public int Bx { get; set; }
    public int k { get; set; }       
    public int sJ { get; set; }      
    public int sBx => Bx - ((1 << 17) - 1); 
}