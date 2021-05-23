﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Dapr;
using Dapr.Client;
using Dapr.Client.Autogen.Grpc.v1;
using DistributedHashMap.Internal;
using StateOptions = Dapr.Client.StateOptions;

namespace DistributedHashMap
{
    public class Map: IMap
    {
        private readonly string _storeName;
        private readonly DaprClient _client;
        private (Header header, string? etag) _headerTuple;
        private bool _rebuilding = false;

        public string Name
        {
            get;
            private set;
        }

        public Map(string name, string storeName, DaprClient client)
        {
            _storeName = storeName;
            _client = client;
            Name = name;
            _headerTuple = (new Header(), null);
        }

        private async Task<Header> GetHeaderFromStore()
        {
            string headerKey = "DHMHeader_" + Name;
            try
            {
                _headerTuple = await _client.GetStateAndETagAsync<Header>(_storeName, headerKey, ConsistencyMode.Strong);
            }
            catch (DaprException)
            {
                _headerTuple = (new Header(), null);
            }

            return _headerTuple.header;
        }

        private async Task<Header> GetHeaderAndMaybeRebuild()
        {
            if (_rebuilding) return _headerTuple.header;

            var header = await GetHeaderFromStore();
            if (!header.Rebuilding)
            {
                return header;
            }

            await Rebuild();
            return await GetHeaderFromStore();
        }

        private int GetMapSize()
        {
            return (int) Math.Pow(2, 7 + _headerTuple.header.Generation);
        }

        private long GetBucket(string key)
        {
            return Murmur3.ComputeHash(key) % GetMapSize();
        }

        private string GetBucketKey(string key)
        {
            return $"DHM_{Name}_{_headerTuple.header.Generation}_{GetBucket(key)}";
        }

        private async Task<bool> PutRaw<T>(string key, T value)
        {
            var header = await GetHeaderAndMaybeRebuild();
            var bucketKey = GetBucketKey(key);
            (Node node, string? etag) nodeItem;
            try
            {
                nodeItem = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong);
            }
            catch (DaprException)
            {
                nodeItem = (new Node(), null);
            }

            var serializedValue = JsonSerializer.Serialize(value);

            if (nodeItem.node.Items.ContainsKey(key) && nodeItem.node.Items[key] == serializedValue)
            {
                return true;
            }

            nodeItem.node.Items.Add(key, serializedValue);
            var saved = await _client.TrySaveStateAsync(_storeName, bucketKey, nodeItem.node, nodeItem.etag,
                new StateOptions {Consistency = ConsistencyMode.Strong, Concurrency = ConcurrencyMode.FirstWrite});

            await UpdateNodeMeta(bucketKey, new NodeMeta {Size = nodeItem.node.Items.Count});

            return saved;
        }

        private Task UpdateNodeMeta(string bucketKey, NodeMeta meta)
        {
            return _client.SaveStateAsync(_storeName, bucketKey + "_meta", meta);
        }

        public async Task Put<T>(string key, T value)
        {
            while (true)
            {
                if (!await PutRaw(key, value)) continue;
                break;
            }
        }

        public async Task<T> Get<T>(string key)
        {
            await GetHeaderAndMaybeRebuild();
            var bucketKey = GetBucketKey(key);
            (Node node, string? etag) bucket;
            try
            {
                bucket = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong);
            }
            catch (DaprException)
            {
                bucket = (new Node(), null);
            }

            var serializedValue = bucket.node.Items.ContainsKey(key) ? bucket.node.Items[key] : null;
            return serializedValue == null ? default : JsonSerializer.Deserialize<T>(serializedValue);
        }

        public async Task<bool> Contains(string key)
        {
            await GetHeaderAndMaybeRebuild();
            var bucketKey = GetBucketKey(key);
            (Node node, string? etag) bucket;
            try
            {
                bucket = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong);
            }
            catch (DaprException)
            {
                bucket = (new Node(), null);
            }

            return bucket.node.Items.ContainsKey(key);
        }

        public async Task Remove(string key)
        {
            await GetHeaderAndMaybeRebuild();
            var bucketKey = GetBucketKey(key);
            (Node node, string? etag) bucket;
            try
            {
                bucket = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong);
            }
            catch (DaprException)
            {
                bucket = (new Node(), null);
            }

            if (!bucket.node.Items.ContainsKey(key)) return;

            bucket.node.Items.Remove(key);

            var saved = await _client.TrySaveStateAsync(_storeName, bucketKey, bucket.node, bucket.etag,
                new StateOptions { Consistency = ConsistencyMode.Strong, Concurrency = ConcurrencyMode.FirstWrite });

            await UpdateNodeMeta(bucketKey, new NodeMeta { Size = bucket.node.Items.Count });
        }

        public async Task Rebuild()
        {
            var header = _headerTuple.header;
            if (!header.Rebuilding)
            {
                header.Rebuilding = true;
                await _client.TrySaveStateAsync(_storeName, "DHMHeader_" + Name, header, _headerTuple.etag);
            }
            var nextGenerationHeader = new Header
            {
                Generation = header.Generation + 1,
                MaxLoad = header.MaxLoad,
                Rebuilding = header.Rebuilding,
                Version = header.Version
            };
            var pointer = (int) (new Random().NextDouble() * GetMapSize());
            var startPoint = pointer;
            var nextGeneration = new Map(Name, _storeName, _client);
            nextGeneration._headerTuple = (nextGenerationHeader, _headerTuple.etag);

            do
            {
                var bucket = await _client.GetStateAndETagAsync<Node>(_storeName,
                    $"DHM_{Name}_{_headerTuple.header.Generation}_{pointer}");
                foreach (var kv in bucket.value.Items)
                {
                    while (!await nextGeneration.PutRaw(kv.Key, kv.Value)) ;
                }

                pointer = (pointer + 1) % GetMapSize();
            } while (pointer != startPoint);
             
            _headerTuple.header.Rebuilding = false;
            _headerTuple.header.Generation++;
            await _client.SaveStateAsync(_storeName, "DHMHeader_" + Name, _headerTuple.header);
        }

        public Task<int> Size()
        {
            throw new NotImplementedException();
        }
    }
}
