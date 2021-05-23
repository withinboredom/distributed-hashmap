using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DistributedHashMap
{
    interface IMap
    {
        public Task Put<T>(string key, T value, CancellationToken cancel = default);

        public Task<T> Get<T>(string key, CancellationToken cancellationToken = default);

        public Task<bool> Contains(string key, CancellationToken cancellationToken = default);

        public Task Remove(string key, CancellationToken cancellationToken = default);

        public Task Rebuild(CancellationToken cancellationToken = default);

        public event EventHandler<bool> IsRebuilding;
    }
}
