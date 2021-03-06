using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DistributedHashMap.Internal
{
    internal class KeyTrigger
    {
        public KeyTrigger(string pubSubName, string topic, Dictionary<string, string>? metadata)
        {
            PubSubName = pubSubName;
            Topic = topic;
            Metadata = metadata;
        }

        [JsonPropertyName("pubsubName")]
        public string PubSubName { get; set; }

        [JsonPropertyName("topic")]
        public string Topic { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, string>? Metadata { get; set; }
    }
}
