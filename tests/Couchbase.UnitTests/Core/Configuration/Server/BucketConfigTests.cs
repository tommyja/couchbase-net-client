using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core;
using Couchbase.Core.CircuitBreakers;
using Couchbase.Core.Configuration.Server;
using Couchbase.Core.DI;
using Couchbase.Core.IO.Connections;
using Couchbase.Core.IO.Transcoders;
using Couchbase.Core.Logging;
using Couchbase.Management.Buckets;
using Couchbase.UnitTests.Utils;
using Couchbase.Utils;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Couchbase.UnitTests.Core.Configuration.Server
{
    public class BucketConfigTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(512)]
        [InlineData(1023)]
        public void When_Rev_Same_And_VBucketMap_Different_Fail(int index)
        {
            var config1 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");
            var config2 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");

            var values = config2.VBucketServerMap.VBucketMap[index];
            config2.VBucketServerMap.VBucketMap[index] = new[] {values[1], values[0]};

            Assert.False(config2.Equals(config1));
        }

        [Fact]
        public void Test_Equals_True()
        {
            var config1 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");
            var config2 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");

            Assert.True(config2.Equals(config1));
        }

        [Fact]
        public void Integration_Test_Environment_HasNodes()
        {
            var config = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\config-integration-tests.json");
            var nodes = config.GetNodes();
            Assert.Equal(2, nodes.Count);
        }

        [Fact]
        public void Test_Equals_False()
        {
            var config1 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");
            var config2 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev96.json");

            Assert.False(config2.Equals(config1));
        }

        [Fact]
        public void Test_Equals_NodesExt_Changed()
        {
            var config1 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");
            var config2 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");

            config2.NodesExt.RemoveAt(0);

            Assert.False(config2.Equals(config1));
        }

        [Fact]
        public void Test_Equals_Node_Changed()
        {
            var config1 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");
            var config2 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");

            config2.Nodes.RemoveAt(0);

            Assert.False(config2.Equals(config1));
        }

        [Fact]
        public void Test_Equals_Node_Value_Changed()
        {
            var config1 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");
            var config2 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");

            config2.Nodes[0].Hostname = "localhost:8091";

            Assert.False(config2.Equals(config1));
        }

       [Theory]
       [InlineData(true)]
       [InlineData(false)]
        public void Test_ClusterNodeChanged(bool hasChanged)
        {
            var config1 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");
            var config2 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");

            if (hasChanged)
            {
                config2.Nodes[0].Hostname = "localhost:8091";
                config2.Equals(config1);
                Assert.True(config2.ClusterNodesChanged);
            }
            else
            {
                config2.Equals(config1);
                Assert.False(config2.ClusterNodesChanged);
            }
        }

        [Fact]
        public void Test_Equals_NodeExt_Changed()
        {
            var config1 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");
            var config2 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");

            config2.NodesExt.RemoveAt(0);

            Assert.False(config2.Equals(config1));
        }

        [Fact]
        public void Test_Equals_NodeExt_Value_Changed()
        {
            var config1 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");
            var config2 = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\rev94.json");

            config2.NodesExt[0].Hostname = "localhost:8091";

            Assert.False(config2.Equals(config1));
        }

        [Theory]
        [InlineData(@"Documents\Configs\rev92-single-node.json", @"Documents\Configs\rev94.json")]
        [InlineData(@"Documents\Configs\rev96.json", @"Documents\Configs\rev98-single-node.json")]
        public async Task Test_Filter_Removed_Nodes(string oldConfigPath, string newConfigPath)
        {
            var oldConfig = ResourceHelper.ReadResource<BucketConfig>(oldConfigPath);
            var newConfig = ResourceHelper.ReadResource<BucketConfig>(newConfigPath);

            var options = new ClusterOptions();
            var bucketNodes = new ConcurrentDictionary<IPEndPoint, IClusterNode>();
            var context = new ClusterContext(new CancellationTokenSource(), options);

            var ipEndpointService = context.ServiceProvider.GetRequiredService<IIpEndPointService>();

            //load up the initial state after bootstrapping
            foreach (var server in oldConfig.NodesExt)
            {
                var endPoint = await ipEndpointService.GetIpEndPointAsync(server).ConfigureAwait(false);
                var clusterNode = new ClusterNode(context, new Mock<IConnectionPoolFactory>().Object,
                    new Mock<ILogger<ClusterNode>>().Object, new Mock<ITypeTranscoder>().Object,
                    new Mock<ICircuitBreaker>().Object,
                    new Mock<ISaslMechanismFactory>().Object,
                    new Mock<IRedactor>().Object,
                    endPoint,
                    BucketType.Couchbase);

                context.AddNode(clusterNode);
                bucketNodes.TryAdd(endPoint, clusterNode);
            }

            foreach (var nodesExt in newConfig.NodesExt)
            {
                var endPoint = await ipEndpointService.GetIpEndPointAsync(nodesExt).ConfigureAwait(false);
                if (bucketNodes.ContainsKey(endPoint))
                {
                    continue;
                }

                var clusterNode = new ClusterNode(context, new Mock<IConnectionPoolFactory>().Object,
                    new Mock<ILogger<ClusterNode>>().Object, new Mock<ITypeTranscoder>().Object,
                    new Mock<ICircuitBreaker>().Object, new Mock<ISaslMechanismFactory>().Object,
                    new Mock<IRedactor>().Object, endPoint, BucketType.Memcached);

                context.AddNode(clusterNode);
                bucketNodes.TryAdd(endPoint, clusterNode);
            }

            await context.PruneNodesAsync(newConfig).ConfigureAwait(false);

            Assert.Equal(newConfig.NodesExt.Count, context.Nodes.Count);
        }

        [Fact]
        public void When_NodesExt_Contains_Evicted_Node_It_Is_Removed()
        {
            //Arrange

            var config = ResourceHelper.ReadResource<BucketConfig>(@"Documents\Configs\config-error.json");
            var originalExtCount = config.NodesExt.Count;
            //Act

            for (int i = 0; i < 10; i++)
            {
                var nodes = config.GetNodes();

                //Assert

                Assert.Equal(3, nodes.Count);
                Assert.Equal(originalExtCount, config.NodesExt.Count);
            }
        }

        [Fact]
        public void When_Config_Has_AlternateAddresses_Hostname_And_Ports_Are_Populated()
        {
            var config = JsonConvert.DeserializeObject<BucketConfig>(ResourceHelper.ReadResource(@"Documents\Configs\config-alternate-addresses.json"));
            config.NetworkResolution = NetworkResolution.Auto;

            Assert.Equal(3, config.Nodes.Count);
            Assert.Equal(3, config.NodesExt.Count);

            foreach (var nodeExt in config.NodesExt)
            {
                var alternateAddress = nodeExt.AlternateAddresses[NetworkResolution.External];
                Assert.NotEmpty(alternateAddress.Hostname);
                Assert.NotNull(alternateAddress.Ports);
            }
        }

        [Fact]
        public void When_Config_Has_AlternateAddresses_NodeAdapter_Uses_External_Hostname()
        {
            var config = JsonConvert.DeserializeObject<BucketConfig>(ResourceHelper.ReadResource(@"Documents\Configs\config-alternate-addresses2.json"));

            var nodes = config.GetNodes();
            var nodesExt = config.NodesExt;

            Assert.Empty(config.Nodes);
            Assert.Equal(3, config.NodesExt.Count);

            for (var i = 0; i < nodes.Count; i++ )
            {
                var alternateAddress = nodesExt[i].AlternateAddresses[NetworkResolution.External];
                Assert.Equal(nodes[i].Hostname, alternateAddress.Hostname);
            }
        }
    }
}
