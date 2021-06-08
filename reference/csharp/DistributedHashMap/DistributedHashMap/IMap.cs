using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DistributedHashMap
{
    interface IMap : IDisposable
    {
        /// <summary>
        /// Put a value into the hashmap
        /// </summary>
        /// <typeparam name="T">The type to put</typeparam>
        /// <param name="key">The item's key</param>
        /// <param name="value">The item</param>
        /// <param name="cancel">A cancellation token</param>
        /// <returns></returns>
        public Task Put<T>(string key, T value, CancellationToken cancel = default);

        /// <summary>
        /// Get a value from the hashmap
        /// </summary>
        /// <typeparam name="T">The type to get</typeparam>
        /// <param name="key">The key to get</param>
        /// <param name="cancellationToken">A cancellation token</param>
        /// <returns>The value for the given key</returns>
        public Task<T> Get<T>(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Whether the hashmap contains a given key
        /// </summary>
        /// <param name="key">The key to check for</param>
        /// <param name="cancellationToken">A cancellation token</param>
        /// <returns>True if there is a key</returns>
        public Task<bool> Contains(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes a key from the hashmap
        /// </summary>
        /// <param name="key">The key to remove</param>
        /// <param name="cancellationToken">A cancellation token</param>
        /// <returns></returns>
        public Task Remove(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Rebuild the hashmap and increase it's potential size by incrementing the generation. Calling this will
        /// cause all writers/readers to participate in a rebuild. Use with caution, will be called automatically as
        /// needed.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token</param>
        /// <returns></returns>
        public Task Rebuild(CancellationToken cancellationToken = default);

        /// <summary>
        /// Get notified that the hashmap is rebuilding. When the event arg is true, it is rebuilding, false it is not.
        /// </summary>
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
        public Task Subscribe(string key, string pubSubName, string topic, Dictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Unsubscribe from key changes
        /// </summary>
        /// <param name="key">The key to unsubscribe from</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task Unsubscribe(string key, CancellationToken cancellationToken = default);
    }
}
