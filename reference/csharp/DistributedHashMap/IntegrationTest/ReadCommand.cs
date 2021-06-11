using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Dapr.Client;
using DistributedHashMap;

namespace IntegrationTest
{
    internal class ReadCommand : ICommand
    {
        public string Command { get; } = "read";

        public async Task<bool> Verify(string lang, string seed, DaprClient client)
        {
            Console.Write($"Verifying {lang}: ");
            var map = new Map($"{lang}{seed}", "statestore", client);
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            for (var i = 0; i < Constants.NumberMessages; i++)
            {
                var verification = await map.Get<int>($"{lang} {i}");
                var contains = await map.Contains($"{lang} {i}");
                if (verification == i && contains) continue;

                Console.WriteLine(
                    $"Failed read verification for {lang}: got {verification} and expected {i}, {(contains ? "contained in map" : "not contained in map")}");
                return false;
            }

            stopwatch.Stop();
            Console.WriteLine($"Done in {stopwatch.Elapsed.TotalSeconds} seconds");
            return true;
        }

        public async Task<int> Run(string[] args)
        {
            var seed = Guid.NewGuid().ToString();
            if (args.Length > 1) seed = args[1];

            var langs = new List<string> {"c#", "php"};
            var client = new DaprClientBuilder().UseGrpcEndpoint("http://localhost:50001").Build();

            foreach (var lang in langs)
            {
                if (! await Verify(lang, seed, client))
                {
                    return 1;
                }
            }

            return 0;
        }
    }
}