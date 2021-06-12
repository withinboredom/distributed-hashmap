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
            var map = new Map<int>($"{lang}{seed}", "statestore", client);
            var stopwatch = new Stopwatch();
            var seen = new List<int>();
            stopwatch.Start();
            await foreach (var (key, value) in map)
            {
                if (seen.Contains(value))
                {
                    Console.WriteLine($"Read duplicate from {lang}: {value}");
                    return false;
                } 
                seen.Add(value);
                if (! await map.Contains(key))
                {
                    Console.WriteLine($"Expected {lang} to contain {key}, but it did not!");
                    return false;
                }

                var result = await map.Get(key);
                if (result != value)
                {
                    Console.WriteLine($"Expected {lang} to contain a value with {key}:{value:N0} but found {result:N0} instead");
                }
            }

            if (seen.Count != Constants.NumberMessages)
            {
                Console.WriteLine($"Failed read verification for {lang}: got {seen.Count:N0} and expected {Constants.NumberMessages:N0}.");
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