using System;
using System.Collections.Generic;
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

        public StateOptions DefaultStateOptions { get; set; } = new StateOptions
        {
            Concurrency = ConcurrencyMode.FirstWrite,
            Consistency = ConsistencyMode.Strong
        };

        private Header DefaultHeader => new Header
        {
            MaxLoad = _maxLoad,
            Generation = (int)Math.Ceiling(Math.Log(Math.Max(256, _expectedCapacity)) / Math.Log(2)) - 7,
        };

        private string HeaderKey => "DHMHeader_" + Name;

        /// <summary>
        /// Retries a block of code
        /// </summary>
        /// <param name="task"></param>
        /// <param name="cancel"></param>
        /// <param name="numberRetries"></param>
        /// <returns></returns>
        private static async Task<bool> DoRetry(Func<Task<bool>> task, CancellationToken cancel, int numberRetries = 100)
        {
            var taskStatus = await task().ConfigureAwait(false);
            if (cancel.IsCancellationRequested || numberRetries < 0)
            {
                return false;
            }

            if (taskStatus) return true;

            return await DoRetry(task, cancel, numberRetries - 1).ConfigureAwait(false);
        }

        public string Name
        {
            get;
        }

        /// <summary>
        /// Create a reference to a hashmap.
        /// </summary>
        /// <param name="name">The name of the hashmap</param>
        /// <param name="storeName">The state store to use</param>
        /// <param name="client">The dapr client to use</param>
        /// <param name="expectedCapacity">The expected initial capacity</param>
        /// <param name="maxLoad">The maximum number of keys to store in a single state key</param>
        public Map(string name, string storeName, DaprClient client, long expectedCapacity = 128, int maxLoad = 12)
        {
            _storeName = storeName;
            _client = client;
            _expectedCapacity = expectedCapacity;
            _maxLoad = maxLoad;
            Name = name;
            _headerTuple = (null, "");
        }

        /// <summary>
        /// Retrieve the header from the store and store it in the store if it doesn't yet exist
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>The header</returns>
        private async Task<Header> GetHeaderFromStore(CancellationToken cancellationToken = default)
        {
            try
            {
                _headerTuple = await _client.GetStateAndETagAsync<Header>(_storeName, HeaderKey, ConsistencyMode.Strong, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (DaprException)
            {
                _headerTuple = (null, "");
            }

            if (_headerTuple.header == null)
            {
                await _client.SaveStateAsync(_storeName, HeaderKey, DefaultHeader,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                _headerTuple.header = DefaultHeader;
            }

            return _headerTuple.header;
        }

        /// <summary>
        /// Retrieve the current header and maybe participate in a rebuild if necessary
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>The current header</returns>
        private async Task<Header> GetHeaderAndMaybeRebuild(CancellationToken cancellationToken = default)
        {
            if (_rebuilding)
            {
                return _headerTuple.header ?? throw new InvalidOperationException("Unable to locate header while rebuilding.");
            }

            var header = await GetHeaderFromStore(cancellationToken).ConfigureAwait(false);
            if (!header.Rebuilding)
            {
                return header;
            }

            await Rebuild(cancellationToken).ConfigureAwait(false);
            return await GetHeaderFromStore(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Get the current map size (in buckets)
        /// </summary>
        /// <returns></returns>
        private int GetMapSize()
        {
            return (int)Math.Pow(2, 7 + _headerTuple.header?.Generation ?? throw new InvalidOperationException("Tried to calculate Map size without reading header first."));
        }

        /// <summary>
        /// Returns a bucket number given a key
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        private long GetBucket(string key)
        {
            return Murmur3.ComputeHash(key) % GetMapSize();
        }

        /// <summary>
        /// Returns the bucket key for reading from the state store
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        private string GetBucketKey(string key)
        {
            return $"DHM_{Name}_{_headerTuple.header?.Generation ?? throw new InvalidOperationException("Tried to get bucket key without reading header first.")}_{GetBucket(key)}";
        }

        /// <summary>
        /// Puts a value in the map
        /// </summary>
        /// <param name="key"></param>
        /// <param name="serializedValue"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<(TriggerEvent?, bool, Dictionary<string, string>?)> PutRaw(string key, string serializedValue, KeyTrigger? subscribe = null, CancellationToken cancellationToken = default)
        {
            var header = await GetHeaderAndMaybeRebuild(cancellationToken).ConfigureAwait(false);
            var bucketKey = GetBucketKey(key);
            (Node? node, string etag) nodeItem;
            try
            {
                nodeItem = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (DaprException)
            {
                nodeItem = (null, "");
            }

            nodeItem.etag = nodeItem.etag == String.Empty ? "-1" : nodeItem.etag;
            nodeItem.node ??= new Node();

            if ((nodeItem.node.Items.ContainsKey(key) && nodeItem.node.Items[key] == serializedValue) && subscribe == null)
            {
                return (null, true, null);
            }

            nodeItem.node.Items.TryGetValue(key, out var prevValue);

            nodeItem.node.Items[key] = serializedValue;
            if (subscribe != null)
            {
                nodeItem.node.Triggers[key] = subscribe;
            }
            nodeItem.node.Triggers.TryGetValue(key, out subscribe);

            var saved = await _client.TrySaveStateAsync(_storeName, bucketKey, nodeItem.node, nodeItem.etag,
               DefaultStateOptions, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (saved && subscribe != null)
            {
                var triggerEvent = new TriggerEvent(key, Name, prevValue, serializedValue, subscribe.PubSubName,
                    subscribe.Topic);

                if (nodeItem.node.Items.Count > header.MaxLoad) await Rebuild(cancellationToken).ConfigureAwait(false);
                return (triggerEvent, saved, subscribe.Metadata);
            }

            if (nodeItem.node.Items.Count > header.MaxLoad) await Rebuild(cancellationToken).ConfigureAwait(false);
            return (null, saved, null);
        }

        /// <summary>
        /// Put a value into the hashmap
        /// </summary>
        /// <typeparam name="T">The type to put</typeparam>
        /// <param name="key">The item's key</param>
        /// <param name="value">The item</param>
        /// <param name="cancel">A cancellation token</param>
        /// <returns></returns>
        public Task Put<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            var serializedValue = JsonSerializer.Serialize(value, _client.JsonSerializerOptions);
            return DoRetry(async () =>
           {
               var status = await PutRaw(key, serializedValue, cancellationToken: cancellationToken).ConfigureAwait(false);
               if (status.Item1 != null)
               {
                   await _client.PublishEventAsync(status.Item1.PubSubName, status.Item1.Topic, status.Item1, status.Item3 ?? new Dictionary<string, string>(),
                       cancellationToken).ConfigureAwait(false);
               }

               return status.Item2;
           }, cancellationToken);
        }

        /// <summary>
        /// Get a value from the hashmap
        /// </summary>
        /// <typeparam name="T">The type to get</typeparam>
        /// <param name="key">The key to get</param>
        /// <param name="cancellationToken">A cancellation token</param>
        /// <returns>The value for the given key</returns>
        public async Task<T> Get<T>(string key, CancellationToken cancellationToken = default)
        {
            await GetHeaderAndMaybeRebuild(cancellationToken).ConfigureAwait(false);
            var bucketKey = GetBucketKey(key);
            (Node? node, string etag) bucket;
            try
            {
                bucket = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong, cancellationToken: cancellationToken).ConfigureAwait(false);
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

        /// <summary>
        /// Whether the hashmap contains a given key
        /// </summary>
        /// <param name="key">The key to check for</param>
        /// <param name="cancellationToken">A cancellation token</param>
        /// <returns>True if there is a key</returns>
        public async Task<bool> Contains(string key, CancellationToken cancellationToken = default)
        {
            await GetHeaderAndMaybeRebuild(cancellationToken).ConfigureAwait(false);
            var bucketKey = GetBucketKey(key);
            (Node node, string? etag) bucket;
            try
            {
                bucket = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (DaprException)
            {
                bucket = (new Node(), null);
            }

            return bucket.node.Items.ContainsKey(key);
        }

        /// <summary>
        /// Removes a key from the hashmap
        /// </summary>
        /// <param name="key">The key to remove</param>
        /// <param name="cancellationToken">A cancellation token</param>
        /// <returns></returns>
        public async Task Remove(string key, CancellationToken cancellationToken = default)
        {
            await GetHeaderAndMaybeRebuild(cancellationToken).ConfigureAwait(false);
            var bucketKey = GetBucketKey(key);
            (Node? node, string etag) bucket;
            try
            {
                bucket = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (DaprException)
            {
                bucket = (new Node(), "");
            }

            bucket.etag = bucket.etag == String.Empty ? "-1" : bucket.etag;
            bucket.node ??= new Node();
            if (!bucket.node.Items.ContainsKey(key)) return;

            bucket.node.Triggers.TryGetValue(key, out var subscribe);
            bucket.node.Items.TryGetValue(key, out var prevValue);

            bucket.node.Items.Remove(key);

            var saved = await _client.TrySaveStateAsync(_storeName, bucketKey, bucket.node, bucket.etag,
                DefaultStateOptions, cancellationToken: cancellationToken).ConfigureAwait(false);

            switch (saved)
            {
                case true when subscribe != null:
                    await _client.PublishEventAsync(subscribe.PubSubName, subscribe.Topic,
                        new TriggerEvent(key, Name, prevValue, null, subscribe.PubSubName, subscribe.Topic), subscribe.Metadata ?? new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);
                    break;
                case false:
                    await Remove(key, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        /// <summary>
        /// Rebuild the hashmap and increase it's potential size by incrementing the generation. Calling this will
        /// cause all writers/readers to participate in a rebuild. Use with caution, will be called automatically as
        /// needed.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token</param>
        /// <returns></returns>
        public async Task Rebuild(CancellationToken cancellationToken = default)
        {
            IsRebuilding?.Invoke(this, true);
            var header = _headerTuple.header ?? await GetHeaderFromStore(cancellationToken).ConfigureAwait(false);
            if (!header.Rebuilding)
            {
                header.Rebuilding = true;
                await _client.TrySaveStateAsync(_storeName, HeaderKey, header, _headerTuple.etag, cancellationToken: cancellationToken).ConfigureAwait(false);
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
                _headerTuple = (nextGenerationHeader, _headerTuple.etag),
                _rebuilding = true
            };

            do
            {
                var (value, _) = await _client.GetStateAndETagAsync<Node>(_storeName,
                    $"DHM_{Name}_{currentGeneration}_{pointer}", cancellationToken: cancellationToken).ConfigureAwait(false);
                if (value != null)
                {
                    foreach (var (key, serializedValue) in value.Items)
                    {
                        await DoRetry(async () =>
                           {
                               var status = await nextGeneration.PutRaw(key, serializedValue,
                                   value.Triggers.ContainsKey(key) ? value.Triggers[key] : null, cancellationToken);
                               value.Triggers.Remove(key);

                               return status.Item2;
                           },
                            cancellationToken, 0).ConfigureAwait(false);
                    }

                    foreach (var (key, trigger) in value.Triggers)
                    {
                        await DoRetry(async () =>
                        {
                            await nextGeneration.Subscribe(key, trigger.PubSubName, trigger.Topic, trigger.Metadata,
                                cancellationToken).ConfigureAwait(false);
                            return true;
                        }, cancellationToken, 0).ConfigureAwait(false);
                    }
                }

                pointer = (pointer + 1) % GetMapSize();
            } while (pointer != startPoint);

            header.Rebuilding = false;
            header.Generation++;
            await _client.SaveStateAsync(_storeName, HeaderKey, _headerTuple.header, cancellationToken: cancellationToken).ConfigureAwait(false);
            IsRebuilding?.Invoke(this, false);
        }

        public event EventHandler<bool> IsRebuilding;

        /// <summary>
        /// Subscribe to key changes via pubsub
        /// </summary>
        /// <param name="key">The key to subscribe to</param>
        /// <param name="pubSubName">The pubsub to broadcast the change to</param>
        /// <param name="topic">The topic to broadcast the change to</param>
        /// <param name="metadata"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task Subscribe(string key, string pubSubName, string topic, Dictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            await GetHeaderAndMaybeRebuild(cancellationToken).ConfigureAwait(false);
            var bucketKey = GetBucketKey(key);
            (Node? node, string etag) bucket;
            try
            {
                bucket = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (DaprException)
            {
                bucket = (new Node(), "");
            }
            bucket.etag = bucket.etag == string.Empty ? "-1" : bucket.etag;
            bucket.node ??= new Node();

            bucket.node.Triggers[key] = new KeyTrigger(pubSubName, topic, metadata);
            await DoRetry(() => _client.TrySaveStateAsync(_storeName, bucketKey, bucket.node, bucket.etag,
                DefaultStateOptions, cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Unsubscribe from key changes
        /// </summary>
        /// <param name="key">The key to unsubscribe from</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task Unsubscribe(string key, CancellationToken cancellationToken = default)
        {
            await GetHeaderAndMaybeRebuild(cancellationToken).ConfigureAwait(false);
            var bucketKey = GetBucketKey(key);
            (Node? node, string etag) bucket;
            try
            {
                bucket = await _client.GetStateAndETagAsync<Node>(_storeName, bucketKey, ConsistencyMode.Strong, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (DaprException)
            {
                bucket = (new Node(), "");
            }
            bucket.etag = bucket.etag == string.Empty ? "-1" : bucket.etag;
            bucket.node ??= new Node();
            if (bucket.node.Triggers.ContainsKey(key))
            {
                bucket.node.Triggers.Remove(key);
            }

            await DoRetry(
                () => _client.TrySaveStateAsync(_storeName, bucketKey, bucket.node, bucket.etag, DefaultStateOptions,
                    cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }
}
