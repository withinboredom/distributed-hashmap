using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace DistributedHashMap.Internal
{
    internal class Node
    {
        [JsonPropertyName("items")]
        public Dictionary<string, string> Items { get; set; } = new Dictionary<string, string>();
    }
}
