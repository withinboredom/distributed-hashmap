using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace DistributedHashMap
{
    interface IMap
    {
        public Task Put<T>(string key, T value);

        public Task<T> Get<T>(string key);

        public Task<bool> Contains(string key);

        public Task Remove(string key);

        public Task Rebuild();
    }
}
