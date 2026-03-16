using System;

public class Program
{
    public static void Main(string[] args)
    {
        string file = "luac.out";

        if (args.Length > 0)
        {
            file = args[0];
        }
        else
        {
            Console.WriteLine("no file specified resorting to luac.out");
        }

        try
        {
            LuaBytecodeParser parser = new LuaBytecodeParser(file);
            parser.Parse();

            Generator generator = new Generator();
            string lua = generator.Generate(
                parser.TopLevel.Instructions,
                parser.TopLevel.Constants,
                parser.TopLevel
            );

            Console.WriteLine("---- decompiled ----");
            Console.WriteLine(lua);
        }
        catch (Exception ex)
        {
            Console.WriteLine("parse error:");
            Console.WriteLine(ex.Message);
        }
    }
}