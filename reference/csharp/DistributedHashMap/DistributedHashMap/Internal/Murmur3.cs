using System;
using System.Collections.Generic;
using System.Text;

namespace DistributedHashMap.Internal
{
    /// <summary>
    /// I couldn't find a murmur3 hash implementation that was correct for dynamic languages, so this is an
    /// implementation that doesn't cast to int, but keeps the 32 bit result as a long.
    /// </summary>
    public class Murmur3
    {
        public static long ComputeHash(string value, int seed = 0)
        {
            var key = Encoding.UTF8.GetBytes(value);
            long h1 = seed < 0 ? -seed : seed;
            var remainder = key.Length & 3;
            var i = 0;
            var k1 = 0L;
            for (var bytes = key.Length - (remainder); i < bytes;)
            {
                k1 = key[i] | (key[++i] << 8) | (key[++i] << 16) | (key[++i] << 24);
                ++i;
                k1 = ((((k1 & 0xffff) * 0xcc9e2d51) + (((((k1 >= 0 ? k1 >> 16 : ((k1 & 0x7fffffff) >> 16) | 0x8000)) * 0xcc9e2d51) & 0xffff) << 16))) & 0xffffffff;
                k1 = k1 << 15 | (k1 >= 0 ? k1 >> 17 : ((k1 & 0x7fffffff) >> 17) | 0x4000);
                k1 = ((((k1 & 0xffff) * 0x1b873593) + (((((k1 >= 0 ? k1 >> 16 : ((k1 & 0x7fffffff) >> 16) | 0x8000)) * 0x1b873593) & 0xffff) << 16))) & 0xffffffff;
                h1 ^= k1;
                h1 = h1 << 13 | (h1 >= 0 ? h1 >> 19 : ((h1 & 0x7fffffff) >> 19) | 0x1000);
                var h1b = ((((h1 & 0xffff) * 5) + (((((h1 >= 0 ? h1 >> 16 : ((h1 & 0x7fffffff) >> 16) | 0x8000)) * 5) & 0xffff) << 16))) & 0xffffffff;
                h1 = (((h1b & 0xffff) + 0x6b64) +
                      (((((h1b >= 0 ? h1b >> 16 : ((h1b & 0x7fffffff) >> 16) | 0x8000)) + 0xe654) & 0xffff) << 16));
            }
            k1 = 0L;
            switch (remainder)
            {
                case 3:
                    k1 ^= key[i + 2] << 16;
                    goto case 2;
                case 2:
                    k1 ^= key[i + 1] << 8;
                    goto case 1;
                case 1:
                    k1 ^= key[i];
                    k1 = (((k1 & 0xffff) * 0xcc9e2d51) + (((((k1 >= 0 ? k1 >> 16 : ((k1 & 0x7fffffff) >> 16) | 0x8000)) * 0xcc9e2d51) & 0xffff) << 16)) & 0xffffffff;
                    k1 = k1 << 15 | (k1 >= 0 ? k1 >> 17 : ((k1 & 0x7fffffff) >> 17) | 0x4000);
                    k1 = (((k1 & 0xffff) * 0x1b873593) + (((((k1 >= 0 ? k1 >> 16 : ((k1 & 0x7fffffff) >> 16) | 0x8000)) * 0x1b873593) & 0xffff) << 16)) & 0xffffffff;
                    h1 ^= k1;
                    break;
            }

            h1 ^= key.Length;
            h1 ^= (h1 >= 0 ? h1 >> 16 : ((h1 & 0x7fffffff) >> 16) | 0x8000);
            h1 = (((h1 & 0xffff) * 0x85ebca6b) + (((((h1 >= 0 ? h1 >> 16 : ((h1 & 0x7fffffff) >> 16) | 0x8000)) * 0x85ebca6b) & 0xffff) << 16)) & 0xffffffff;
            h1 ^= (h1 >= 0 ? h1 >> 13 : ((h1 & 0x7fffffff) >> 13) | 0x40000);
            h1 = ((((h1 & 0xffff) * 0xc2b2ae35) + (((((h1 >= 0 ? h1 >> 16 : ((h1 & 0x7fffffff) >> 16) | 0x8000)) * 0xc2b2ae35) & 0xffff) << 16))) & 0xffffffff;
            h1 ^= (h1 >= 0 ? h1 >> 16 : ((h1 & 0x7fffffff) >> 16) | 0x8000);

            return h1;
        }
    }
}
