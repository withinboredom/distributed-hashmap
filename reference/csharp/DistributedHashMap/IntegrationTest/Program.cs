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
        static async Task<int> Main(string[] args)
        {
            if (args.Length > 0)
            {
                switch (args[0])
                {
                    case "write":
                        Console.WriteLine("Starting message write");
                        var stopwatch = new Stopwatch();
                        stopwatch.Start();

                        Func<int, Task> writer = async (int message) =>
                        {
                            var map = new Map("c#", "statestore", new DaprClientBuilder().Build());
                            await map.Put("c# " + message, message);
                        };
                        var messages = new List<Task>();
                        for (var i = 0; i < 2000; i++)
                        {
                            messages.Add(writer(i));
                        }

                        await Task.WhenAll(messages);
                        stopwatch.Stop();
                        Console.WriteLine($"2000 messages written in {stopwatch.Elapsed.TotalSeconds:N4} seconds");
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
