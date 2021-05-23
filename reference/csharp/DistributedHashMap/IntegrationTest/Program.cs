using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Dapr.Client;
using DistributedHashMap;

namespace IntegrationTest
{
    class Program
    {
        private const int NumberMessages = 1000;

        static async Task<int> Main(string[] args)
        {
            if (args.Length > 0)
            {
                switch (args[0])
                {
                    case "write":
                        var seed = Guid.NewGuid().ToString();
                        if (args.Length > 1)
                        {
                            seed = args[1];
                        }
                        Console.WriteLine("Starting message write");
                        var stopwatch = new Stopwatch();
                        var messageObserver = new ObservableCollection<int>();
                        var writtenMessages = 0;
                        var rebuildingStopwatch = new Stopwatch();
                        messageObserver.CollectionChanged += (sender, eventArgs) =>
                        {
                            var every = (int) Math.Round(NumberMessages * 0.01f);
                            writtenMessages += eventArgs.NewItems?.Count ?? 0;
                            if (writtenMessages % every == 0) Console.WriteLine($"Wrote {every} messages ({writtenMessages} total).");
                        };

                        Func<int, Map, Task> writer = async (int message, Map map) =>
                        {
                            await map.Put("c# " + message, message);
                            messageObserver.Add(1);
                        };
                        var messages = new List<Task>();
                        var client = new DaprClientBuilder().Build();

                        stopwatch.Start();

                        var result = Parallel.For(0, NumberMessages, new ParallelOptions
                        {
                            MaxDegreeOfParallelism = 12
                        }, (int i, ParallelLoopState state) =>
                        {
                            var map = new Map("c#" + seed, "statestore", client, expectedCapacity: NumberMessages);
                            map.IsRebuilding += (sender, b) =>
                            {
                                if (b && !rebuildingStopwatch.IsRunning)
                                {
                                    rebuildingStopwatch.Start();
                                    Console.Write("Rebuilding... ");
                                } else if (!b && rebuildingStopwatch.IsRunning)
                                {
                                    rebuildingStopwatch.Stop();
                                    Console.WriteLine($"completed in {rebuildingStopwatch.Elapsed.TotalSeconds:N2} seconds.");
                                }
                            };
                            writer(i, map).Wait();
                        });
                        stopwatch.Stop();
                        
                        Console.WriteLine($"{writtenMessages}/{NumberMessages} messages written in {stopwatch.Elapsed.TotalSeconds:N4} seconds");
                        return 0;
                    case "read":
                        return 0;
                }
            }

            Console.WriteLine("Usage: IntegrationTest [write|read]");
            return 1;
        }
    }
}
