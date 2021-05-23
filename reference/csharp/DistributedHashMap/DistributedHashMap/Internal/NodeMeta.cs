using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace DistributedHashMap.Internal
{
    internal class NodeMeta
    {
        [JsonPropertyName("size")] public int Size { get; set; } = 0;
    }
}
