using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Couchbase.Core;
using Couchbase.Core.CircuitBreakers;
using Couchbase.Core.Configuration.Server;
using Couchbase.Core.DI;
using Couchbase.Core.IO.Connections;
using Couchbase.Core.IO.Transcoders;
using Couchbase.Core.Logging;
using Couchbase.Management.Buckets;
using Couchbase.Utils;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Couchbase.UnitTests.Utils
{
    public class ArrayExtensionsTests
    {
        private static readonly Encoding Utf8NoBomEncoding = new UTF8Encoding(false);

        #region IsJson

        [Theory]
        [InlineData("", 0, 0, false)]
        [InlineData("a", 0, 1, false)]
        [InlineData("abc", 0, 3, false)]
        [InlineData("[]", 0, 2, true)]
        [InlineData("{}", 0, 2, true)]
        [InlineData("xx{\"a\":1}yy", 2, 7, true)]
        [InlineData("xx[\"abc\"]yy", 2, 7, true)]
        public void IsJson_ExpectedResult(string value, int offset, int length, bool expectedResult)
        {
            // Arrange

            var bytes = Utf8NoBomEncoding.GetBytes(value);

            // Act

            var result = bytes.AsSpan(offset, length).IsJson();

            // Assert

            Assert.Equal(expectedResult, result);
        }

        #endregion

        [Fact]
        public void GetRandom_Where_Clause()
        {
            var dict = new Dictionary<string, ClusterNode>
            {
                {"127.0.0.1", MakeFakeClusterNode() },
                {"127.0.0.2", MakeFakeClusterNode() },
                {"127.0.0.3", MakeFakeClusterNode() }
            };

            var node = dict.GetRandom(x => x.Value.HasViews);

            Assert.True(node.Value.HasViews);
        }

        [Fact]
        public void GetRandom_Where_Clause_No_Matches()
        {
            var dict = new Dictionary<string, ClusterNode>
            {
                {"127.0.0.1", MakeFakeClusterNode() },
                {"127.0.0.2", MakeFakeClusterNode() },
                {"127.0.0.3", MakeFakeClusterNode() }
            };

            var node = dict.GetRandom(x => x.Value.HasAnalytics);

            Assert.Null(node.Value);
        }

        #region Helpers

        private ClusterNode MakeFakeClusterNode()
        {
            return new ClusterNode(
                new ClusterContext(null, new ClusterOptions()),
                new Mock<IConnectionPoolFactory>().Object,
                new Mock<ILogger<ClusterNode>>().Object,
                new Mock<ITypeTranscoder>().Object,
                new Mock<ICircuitBreaker>().Object,
                new Mock<ISaslMechanismFactory>().Object,
                new Mock<IRedactor>().Object,
                new IPEndPoint(IPAddress.Parse("127.0.0.1"), 11210),
                BucketType.Couchbase)
            {
                NodesAdapter = new NodeAdapter
                {
                    Hostname = "localhost",
                    Views = 8092
                }
            };
        }

        #endregion
    }
}
