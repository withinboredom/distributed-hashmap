using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Dapr.Client;
using DistributedHashMap;

namespace IntegrationTest
{
    class Program
    {
        private const int NumberMessages = 2000;

        static async Task<int> Main(string[] args)
        {
            if (args.Length > 0)
            {
                var delete = false;
                switch (args[0])
                {
                    case "delete":
                        delete = true;
                        goto case "write";
                    case "write":
                        var seed = Guid.NewGuid().ToString();
                        if (args.Length > 1)
                        {
                            seed = args[1];
                        }
                        Console.WriteLine("Starting message write");
                        var stopwatch = new Stopwatch();
                        var writtenMessages = 0;
                        var rebuildingStopwatch = new Stopwatch();

                        Func<int, Map, Task> writer = async (int message, Map map) =>
                        {
                            if (!delete)
                            {
                                await map.Subscribe("c# " + message, "pubsub", "c#");
                                await map.Put("c# " + message, message);
                            }
                            else await map.Remove("c# " + message);
                            var every = (int)Math.Round(NumberMessages * 0.1f);
                            if (++writtenMessages % every == 0) Console.WriteLine($"Written {writtenMessages} messages.");
                        };
                        var client = new DaprClientBuilder().UseGrpcEndpoint("http://localhost:50001").Build();

                        stopwatch.Start();

                        var result = Parallel.For(0, NumberMessages, new ParallelOptions
                        {
                            MaxDegreeOfParallelism = 1
                        }, (int i, ParallelLoopState state) =>
                        {
                            var map = new Map("c#" + seed, "statestore", client);
                            map.IsRebuilding += (sender, b) =>
                            {
                                switch (b)
                                {
                                    case true when !rebuildingStopwatch.IsRunning:
                                        rebuildingStopwatch.Start();
                                        Console.Write("Rebuilding... ");
                                        break;
                                    case false when rebuildingStopwatch.IsRunning:
                                        rebuildingStopwatch.Stop();
                                        Console.WriteLine($"completed in {rebuildingStopwatch.Elapsed.TotalSeconds:N2} seconds.");
                                        break;
                                }
                            };
                            writer(i, map).Wait();
                        });
                        stopwatch.Stop();

                        Console.WriteLine($"{writtenMessages}/{NumberMessages} messages written in {stopwatch.Elapsed.TotalSeconds:N4} seconds");

                        stopwatch.Restart();
                        Console.Write("Verifying... ");
                        var map = new Map("c#" + seed, "statestore", client);
                        for (var i = 0; i < NumberMessages; i++)
                        {
                            var verification = await map.Get<int>("c# " + i);
                            if (verification != i)
                            {
                                Console.WriteLine($"failed. (got {verification} and expecting {i})");
                                return 1;
                            }
                        }
                        stopwatch.Stop();
                        Console.WriteLine($"verified in {stopwatch.Elapsed.TotalSeconds:N2} seconds!");

                        return 0;
                    case "read":
                        seed = Guid.NewGuid().ToString();
                        if (args.Length > 1)
                        {
                            seed = args[1];
                        }

                        var langs = new List<string> { "c#", "php" };
                        client = new DaprClientBuilder().UseGrpcEndpoint("http://localhost:50001").Build();

                        foreach (var lang in langs)
                        {
                            Console.Write($"Verifying {lang}: ");
                            map = new Map($"{lang}{seed}", "statestore", client);
                            stopwatch = new Stopwatch();
                            stopwatch.Start();
                            for (var i = 0; i < NumberMessages; i++)
                            {
                                var verification = await map.Get<int>($"{lang} {i}");
                                var contains = await map.Contains($"{lang} {i}");
                                if (verification == i && contains) continue;

                                Console.WriteLine($"Failed read verification for {lang} and got {verification} instead of {i}");
                                return 1;
                            }
                            stopwatch.Stop();
                            Console.WriteLine($"Done in {stopwatch.Elapsed.TotalSeconds} seconds");
                        }

                        return 0;
                }
            }

            Console.WriteLine("Usage: IntegrationTest [write|read]");
            return 1;
        }
    }
}
