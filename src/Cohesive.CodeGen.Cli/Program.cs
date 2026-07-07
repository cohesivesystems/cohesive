namespace Cohesive.CodeGen.Cli;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (!CodeGenCliParser.TryParse(args, out var options, out var error, out var showHelp))
            {
                if (showHelp)
                {
                    CodeGenUsage.WriteTo(Console.Out);
                    return 0;
                }

                Console.Error.WriteLine(error);
                CodeGenUsage.WriteTo(Console.Error);
                return 2;
            }

            _ = ContractsCodeGenerator.Generate(options!, Console.Out);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
