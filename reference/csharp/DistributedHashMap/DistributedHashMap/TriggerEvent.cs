using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DistributedHashMap
{
    public class TriggerEvent
    {
        public TriggerEvent(string key, string mapName, string? previousValue, string? newValue, string pubSubName, string topic)
        {
            Key = key;
            MapName = mapName;
            PreviousValue = previousValue;
            NewValue = newValue;
            PubSubName = pubSubName;
            Topic = topic;
        }

        [JsonPropertyName("key")]
        public string Key { get; set; }

        [JsonPropertyName("mapName")]
        public string MapName { get; set; }

        [JsonPropertyName("previousValue")]
        public string? PreviousValue { get; set; }

        [JsonPropertyName("newValue")]
        public string? NewValue { get; set; }

        [JsonPropertyName("pubsubName")]
        public string PubSubName { get; set; }

        [JsonPropertyName("topic")]
        public string Topic { get; set; }
    }
}
