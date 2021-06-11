using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IntegrationTest
{
    internal class Program
    {
        private static int Usage()
        {
            Console.WriteLine("Usage: IntegrationTest [write|read]");
            return 1;
        }

        private static async Task<int> Main(string[] args)
        {
            var commands = new List<ICommand> {new ReadCommand(), new WriteCommand()};

            if (args.Length <= 0) return Usage();

            var command = commands.Find(x => x.Command == args[0]);
            return command == null ? Usage() : await command.Run(args);
        }
    }
}