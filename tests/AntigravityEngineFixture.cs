using System;
using System.IO;
using System.Threading;

internal static class AntigravityEngineFixture
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine("1.5.18");
            return 0;
        }
        if (args.Length == 1 && args[0] == "models")
        {
            if (File.Exists("signed-out.fixture"))
            {
                Console.Error.WriteLine("Error: Please sign in to view available models. Launch the CLI without arguments to sign in.");
                return 1;
            }
            Console.WriteLine("gemini-3.8-flash        Gemini 3.8 Flash");
            Console.WriteLine("example-model");
            return 0;
        }
        if (args.Length > 0)
            File.WriteAllLines("agy-launch.txt", args);
        Console.WriteLine("Antigravity fixture ready");
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
