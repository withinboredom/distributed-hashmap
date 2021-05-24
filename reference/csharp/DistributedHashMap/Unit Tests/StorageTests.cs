using System;
using System.Threading.Tasks;
using Dapr.Client;
using DistributedHashMap;
using Xunit;

namespace Unit_Tests
{
    public class StorageTests
    {
        class obj
        {
            public string Thing { get; set; }
        }

        private readonly DaprClient _client;
        private const string StoreName = "statestore";

        public StorageTests()
        {
            _client = new DaprClientBuilder().UseGrpcEndpoint("http://localhost:50001").Build();
        }

        [Fact]
        public async Task LoadNullValue()
        {
            var map = new Map(nameof(LoadNullValue), StoreName, _client);
            var result = await map.Get<obj>("key");
            Assert.Null(result);
        }

        [Fact]
        public async Task SaveAndLoadValue()
        {
            var map = new Map(nameof(SaveAndLoadValue), StoreName, _client);
            var obj = new obj {Thing = Guid.NewGuid().ToString()};
            await map.Put("test", obj);
            Assert.True(await map.Contains("test"));
            var result = await map.Get<obj>("test");
            Assert.Equal(obj.Thing, result.Thing);
        }

        [Fact]
        public async Task SaveAndRemoveValue()
        {
            var map = new Map(nameof(SaveAndRemoveValue), StoreName, _client);
            var obj = "stuff";
            await map.Put("test", obj);
            Assert.Equal("stuff", await map.Get<string>("test"));
            await map.Remove("test");
            Assert.False(await map.Contains("test"));
            Assert.Null(await map.Get<string>("test"));
        }

        [Fact]
        public async Task RebuildingSlow()
        {
            var name = nameof(RebuildingSlow) + new Random().Next();
            var map = new Map(name, StoreName, _client);
            for (var i = 0; i < 200; i++)
            {
                await map.Put($"test_{i}", i);
            }
            await map.Rebuild();
            for (var i = 0; i < 200; i++)
            {
                Assert.Equal(i, await map.Get<int>($"test_{i}"));
            }
        }
    }
}
