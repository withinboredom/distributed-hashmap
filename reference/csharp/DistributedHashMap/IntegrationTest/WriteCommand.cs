using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Dapr.Client;
using DistributedHashMap;

namespace IntegrationTest
{
    internal class WriteCommand : ICommand
    {
        public string Command { get; } = "write";

        public async Task<int> Run(string[] args)
        {
            var seed = Guid.NewGuid().ToString();
            if (args.Length > 1) seed = args[1];
            Console.WriteLine("Starting message write");
            var stopwatch = new Stopwatch();
            var writtenMessages = 0;
            var rebuildingStopwatch = new Stopwatch();

            Func<int, Map, Task> writer = async (message, map) =>
            {
                await DoWrite(map, message);
                var every = (int) Math.Round(Constants.NumberMessages * 0.1f);
                if (++writtenMessages % every == 0) Console.WriteLine($"Written {writtenMessages:N} messages.");
            };
            var client = new DaprClientBuilder().UseGrpcEndpoint("http://localhost:50001").Build();

            stopwatch.Start();

            var result = Parallel.For(0, Constants.NumberMessages, new ParallelOptions
            {
                MaxDegreeOfParallelism = 1
            }, (i, state) =>
            {
                var map = new Map("c#" + seed, Constants.Store, client);
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

            Console.WriteLine(
                $"{writtenMessages}/{Constants.NumberMessages} messages written in {stopwatch.Elapsed.TotalSeconds:N4} seconds");

            stopwatch.Restart();
            Console.Write("Verifying... ");
            var verified = await new ReadCommand().Verify("c#", seed, client);
            if (!verified) return 1;

            stopwatch.Stop();
            Console.WriteLine($"verified in {stopwatch.Elapsed.TotalSeconds:N2} seconds!");

            return 0;
        }

        protected async Task DoWrite(Map map, int message)
        {
            await map.Subscribe("c# " + message, Constants.PubSub, "changes");
            await map.Put("c# " + message, message);
        }
    }
}