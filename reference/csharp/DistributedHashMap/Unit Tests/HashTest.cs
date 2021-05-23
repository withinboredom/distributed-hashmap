using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DistributedHashMap.Internal;
using Xunit;
using static Xunit.Assert;

namespace Unit_Tests
{
    public class HashTest
    {
        [Fact]
        public void VerifyHash()
        {
            var hash = Murmur3.ComputeHash("test");
            Equal(3127628307, hash);
        }
    }
}
