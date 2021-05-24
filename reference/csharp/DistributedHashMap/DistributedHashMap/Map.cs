using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapr;
using Dapr.Client;
using DistributedHashMap.Internal;
using StateOptions = Dapr.Client.StateOptions;

namespace DistributedHashMap
{
    public class Map : IMap
    {
        private readonly string _storeName;
        private readonly DaprClient _client;
        private readonly long _expectedCapacity;
        private readonly int _maxLoad;
        private (Header? header, string etag) _headerTuple;
        private bool _rebuilding;

        private Header DefaultHeader => new Header
        {
            MaxLoad = _maxLoad,
            Generation = (int) Math.Ceiling(Math.Log(Math.Max(256, _expectedCapacity)) / Math.Log(2)) - 7,
        };

        private string HeaderKey => "DHMHeader_" + Name;

        private static async Task<bool> DoRetry(Func<Task<bool>> task, CancellationToken cancel, int numberRetries = 100)
        {
            var taskStatus = await task();
            if (cancel.IsCancellationRequested || numberRetries < 0)
            {
                return false;
            }

            if (taskStatus) return true;

            return await DoRetry(task, cancel, numberRetries - 1);
        }

        public string Name
        {
            get;
        }

        public Map(string name, string storeName, DaprClient client, long expectedCapacity = 128, int maxLoad = 12)
        {
            _storeName = storeName;
            _client = client;
            _expectedCapacity = expectedCapacity;
            _maxLoad = maxLoad;
            Name = name;
            _headerTuple = (null, "");
        }

        private async Task<Header> GetHeaderFromStore(CancellationToken cancellationToken = default)
        {
            try
            {
                _headerTuple = await _client.GetStateAndETagAsync<Header>(_storeName, HeaderKey, ConsistencyMode.Strong, cancellationToken: cancellationToken);
            }
            catch (DaprException)
            {
                _headerTuple = (null, "");
            }

            if (_headerTuple.header == null)
            {
                await _client.SaveStateAsync(_storeName, HeaderKey, DefaultHeader,
                    cancellationToken: cancellationToken);
                _headerTuple.header = DefaultHeader;
            }

            return _headerTuple.header;
        }

        private async Task<Header> GetHeaderAndMaybeRebuild(CancellationToken cancellationToken = default)
        {
            if (_rebuilding)
            {
                return _headerTuple.header ?? throw new InvalidOperationException("Unable to locate header while rebuilding.");
            }

            var header = await GetHeaderFromStore(cancellationToken);
            if (!header.Rebuilding)
            {
                return header;
            }

            await Rebuild(cancellationToken);
            return await GetHeaderFromStore(cancellationToken);
        }

        private int GetMapSize()
        {
            return (int)Math.Pow(2, 7 + _headerTuple.header?.Generation ?? throw new InvalidOperationException("Tried to calculate Map size without reading header first."));
        }

        private long GetBucket(string key)
        {
            return Murmur3.ComputeHash(key) % GetMapSize();
        }

        private string GetBucketKey(string key)
        {
            return $"DHM_{Name}_{_headerTuple.header?.Generation ?? throw new InvalidOperationException("Tried to get bucket key without reading header first.")}_{GetBucket(key)}";
        }

        private async Task<bool> PutRaw(string key, string serializedValue, CancellationToken cancellationToken = default)
        {
            var header = await GetHeaderAndMaybeRebuild(cancellationToken);
            var bucketKey = GetBucketKey(key);
            (Node? node, string etag) nodeItem;
            try
            {
                nodeItem = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong, cancellationToken: cancellationToken);
            }
            catch (DaprException)
            {
                nodeItem = (null, "");
            }

            nodeItem.etag = nodeItem.etag == String.Empty ? "-1" : nodeItem.etag;
            nodeItem.node ??= new Node();

            if (nodeItem.node.Items.ContainsKey(key) && nodeItem.node.Items[key] == serializedValue)
            {
                return true;
            }

            nodeItem.node.Items[key] = serializedValue;
            var saved = await _client.TrySaveStateAsync(_storeName, bucketKey, nodeItem.node, nodeItem.etag,
                new StateOptions { Consistency = ConsistencyMode.Strong, Concurrency = ConcurrencyMode.FirstWrite }, cancellationToken: cancellationToken);

            if (nodeItem.node.Items.Count > header.MaxLoad) await Rebuild(cancellationToken);

            return saved;
        }

        public Task Put<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            var serializedValue = JsonSerializer.Serialize(value, _client.JsonSerializerOptions);
            return DoRetry(() => PutRaw(key, serializedValue, cancellationToken), cancellationToken);
        }

        public async Task<T> Get<T>(string key, CancellationToken cancellationToken = default)
        {
            await GetHeaderAndMaybeRebuild(cancellationToken);
            var bucketKey = GetBucketKey(key);
            (Node? node, string etag) bucket;
            try
            {
                bucket = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong, cancellationToken: cancellationToken);
            }
            catch (DaprException)
            {
                bucket = (null, "");
            }
            bucket.node ??= new Node();

            if (!bucket.node.Items.ContainsKey(key))
            {
                return default!;
            }

            var serializedValue = bucket.node.Items[key];
            return JsonSerializer.Deserialize<T>(serializedValue, _client.JsonSerializerOptions)!;
        }

        public async Task<bool> Contains(string key, CancellationToken cancellationToken = default)
        {
            await GetHeaderAndMaybeRebuild(cancellationToken);
            var bucketKey = GetBucketKey(key);
            (Node node, string? etag) bucket;
            try
            {
                bucket = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong, cancellationToken: cancellationToken);
            }
            catch (DaprException)
            {
                bucket = (new Node(), null);
            }

            return bucket.node.Items.ContainsKey(key);
        }

        public async Task Remove(string key, CancellationToken cancellationToken = default)
        {
            await GetHeaderAndMaybeRebuild(cancellationToken);
            var bucketKey = GetBucketKey(key);
            (Node? node, string etag) bucket;
            try
            {
                bucket = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong, cancellationToken: cancellationToken);
            }
            catch (DaprException)
            {
                bucket = (new Node(), "");
            }

            bucket.etag = bucket.etag == String.Empty ? "-1" : bucket.etag;
            if (!bucket.node.Items.ContainsKey(key)) return;

            bucket.node.Items.Remove(key);

            var saved = await _client.TrySaveStateAsync(_storeName, bucketKey, bucket.node, bucket.etag,
                new StateOptions { Consistency = ConsistencyMode.Strong, Concurrency = ConcurrencyMode.FirstWrite }, cancellationToken: cancellationToken);

            if (!saved)
            {
                await Remove(key, cancellationToken);
            }
        }

        public async Task Rebuild(CancellationToken cancellationToken = default)
        {
            IsRebuilding?.Invoke(this, true);
            var header = _headerTuple.header ?? await GetHeaderFromStore(cancellationToken);
            if (!header.Rebuilding)
            {
                header.Rebuilding = true;
                await _client.TrySaveStateAsync(_storeName, HeaderKey, header, _headerTuple.etag, cancellationToken: cancellationToken);
            }
            var nextGenerationHeader = new Header
            {
                Generation = header.Generation + 1,
                MaxLoad = header.MaxLoad,
                Rebuilding = header.Rebuilding,
                Version = header.Version
            };
            var pointer = (int)(new Random().NextDouble() * GetMapSize());
            var startPoint = pointer;
            var currentGeneration = header.Generation;
            var nextGeneration = new Map(Name, _storeName, _client)
            {
                _headerTuple = (nextGenerationHeader, _headerTuple.etag), _rebuilding = true
            };

            do
            {
                var (value, _) = await _client.GetStateAndETagAsync<Node>(_storeName,
                    $"DHM_{Name}_{currentGeneration}_{pointer}", cancellationToken: cancellationToken);
                if (value != null)
                {
                    foreach (var (key, serializedValue) in value.Items)
                    {
                        await DoRetry(() => nextGeneration.PutRaw(key, serializedValue, cancellationToken),
                            cancellationToken, 0);
                    }
                }

                pointer = (pointer + 1) % GetMapSize();
            } while (pointer != startPoint);

            header.Rebuilding = false;
            header.Generation++;
            await _client.SaveStateAsync(_storeName, HeaderKey, _headerTuple.header, cancellationToken: cancellationToken);
            IsRebuilding?.Invoke(this, false);
        }

        public event EventHandler<bool> IsRebuilding;
    }
}
