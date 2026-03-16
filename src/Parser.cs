using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

public class LuaBytecodeParser
{
    private BinaryReader reader;
    private int sizeTSize;

    public Function TopLevel;

    public LuaBytecodeParser(string file)
    {
        reader = new BinaryReader(File.OpenRead(file));
    }

    public void Parse()
    {
        ReadHeader();
        TopLevel = ReadFunction();
    }

    void ReadHeader()
    {
        byte[] signature = reader.ReadBytes(4);
        byte version = reader.ReadByte();
        byte format = reader.ReadByte();
        byte endianness = reader.ReadByte();
        byte intSize = reader.ReadByte();
        sizeTSize = reader.ReadByte();
        byte instructionSize = reader.ReadByte();
        byte numberSize = reader.ReadByte();
        byte integralFlag = reader.ReadByte();

        Console.WriteLine("Header:");
        Console.WriteLine($"Signature: {Encoding.ASCII.GetString(signature)}");
        Console.WriteLine($"Version: {version:X}");
        Console.WriteLine($"Format: {format}");
        Console.WriteLine($"Endianness: {endianness}");
        Console.WriteLine($"Int Size: {intSize}");
        Console.WriteLine($"size_t Size: {sizeTSize}");
        Console.WriteLine($"Instruction Size: {instructionSize}");
        Console.WriteLine($"Number Size: {numberSize}");
        Console.WriteLine();
    }

    Function ReadFunction()
    {
        var func = new Function();

        string source = ReadString();

        int lineDefined = reader.ReadInt32();
        int lastLineDefined = reader.ReadInt32();

        byte nUpvalues = reader.ReadByte();
        func.NumParams = reader.ReadByte();
        byte isVararg = reader.ReadByte();
        byte maxStackSize = reader.ReadByte();

        Console.WriteLine("function:");
        Console.WriteLine($"source: {source}");
        Console.WriteLine($"params: {func.NumParams}");
        Console.WriteLine($"stackSize: {maxStackSize}");

        int instructionCount = reader.ReadInt32();

        Console.WriteLine($"instructions: {instructionCount}\n");
        Console.WriteLine();

        for (int i = 0; i < instructionCount; i++)
        {
            uint raw = reader.ReadUInt32();

            int opcode = (int)(raw & 0x3F);
            int A = (int)((raw >> 6) & 0xFF);
            int C = (int)((raw >> 14) & 0x1FF);
            int B = (int)((raw >> 23) & 0x1FF);
            int Bx = (int)((raw >> 14) & 0x3FFFF);
            int sBx = Bx - 131071;

            string name = Lookup.GetName(opcode);

            func.Instructions.Add(new Instruction
            {
                Opcode = name,
                A = A,
                B = B,
                C = C,
                Bx = Bx,
            });

            Console.WriteLine($"{i:D4} {name} A:{A} B:{B} C:{C}");
        }

        int constantCount = reader.ReadInt32();

        Console.WriteLine();
        Console.WriteLine($"constants: {constantCount}");

        for (int i = 0; i < constantCount; i++)
        {
            byte type = reader.ReadByte();

            switch (type)
            {
                case 0: func.Constants.Add(null); break;
                case 1: func.Constants.Add(reader.ReadByte() != 0); break;
                case 3: func.Constants.Add(reader.ReadDouble()); break;
                case 4: func.Constants.Add(ReadString()); break;
            }

            Console.WriteLine($"K{i}: {func.Constants[i]}");
        }

        int protoCount = reader.ReadInt32();
        for (int i = 0; i < protoCount; i++)
            func.Protos.Add(ReadFunction());

        int lineInfoCount = reader.ReadInt32();
        reader.ReadBytes(lineInfoCount * 4);

        int localCount = reader.ReadInt32();
        for (int i = 0; i < localCount; i++)
        {
            string localName = ReadString();
            func.LocalNames.Add(localName);
            reader.ReadInt32();
            reader.ReadInt32();
        }

        int upvalueCount = reader.ReadInt32();
        for (int i = 0; i < upvalueCount; i++)
            func.UpvalueNames.Add(ReadString());

        return func;
    }

    ulong ReadSizeT()
    {
        if (sizeTSize == 8)
            return reader.ReadUInt64();
        else
            return reader.ReadUInt32();
    }

    string ReadString()
    {
        ulong size = ReadSizeT();

        if (size == 0)
            return null;

        byte[] bytes = reader.ReadBytes((int)size);

        return Encoding.UTF8.GetString(bytes, 0, (int)size - 1);
    }
}