using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace DistributedHashMap.Internal
{
    internal class Header
    {
        /// <summary>
        /// Whether the map is being rebuilt
        /// </summary>
        [JsonPropertyName("rebuilding")] public bool Rebuilding { get; set; } = false;

        /// <summary>
        /// The current generation of the map
        /// </summary>
        [JsonPropertyName("generation")] public int Generation { get; set; } = 1;

        /// <summary>
        /// The max number of keys in a bucket
        /// </summary>
        [JsonPropertyName("maxLoad")] public int MaxLoad { get; set; } = 12;

        /// <summary>
        /// The implementation version
        /// </summary>
        [JsonPropertyName("version")] public int Version { get; set; } = 0;
    }
}
