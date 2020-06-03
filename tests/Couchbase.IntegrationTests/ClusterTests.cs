using System;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Diagnostics;
using Couchbase.IntegrationTests.Fixtures;
using Xunit;

namespace Couchbase.IntegrationTests
{
    public class ClusterTests : IClassFixture<ClusterFixture>
    {
        private readonly ClusterFixture _fixture;

        public ClusterTests(ClusterFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Test_Open_More_Than_One_Bucket()
        {
            var cluster = await _fixture.GetCluster().ConfigureAwait(false);
            var key = Guid.NewGuid().ToString();

            var bucket1 = await cluster.BucketAsync("travel-sample").ConfigureAwait(false);
            Assert.NotNull(bucket1);

            var bucket2 = await cluster.BucketAsync("default").ConfigureAwait(false);
            Assert.NotNull(bucket2);

            try
            {
                var result1 = await bucket1.DefaultCollection().InsertAsync(key, new {Whoah = "buddy!"})
                    .ConfigureAwait(false);

                var result2 = await bucket2.DefaultCollection().InsertAsync(key, new { Whoah = "buddy!" })
                    .ConfigureAwait(false);
            }
            finally
            {
                await bucket1.DefaultCollection().RemoveAsync(key).ConfigureAwait(false);
                await bucket2.DefaultCollection().RemoveAsync(key).ConfigureAwait(false);
            }
        }

        [Fact]
        public async Task Test_Query_With_Positional_Parameters()
        {
            var cluster = await _fixture.GetCluster().ConfigureAwait(false);;
            var result = await cluster.QueryAsync<dynamic>("SELECT x.* FROM `default` WHERE x.Type=$1",
                options =>
                {
                    options.Parameter("foo");
                }).ConfigureAwait(true);

            await foreach (var row in result)
            {
            }
            result.Dispose();
        }

        [Fact]
        public async Task Test_Query2()
        {
            var cluster = await _fixture.GetCluster().ConfigureAwait(false);

            var result = await cluster.QueryAsync<dynamic>("SELECT * FROM `default` WHERE type=$name;",
                options =>
            {
                options.Parameter("name", "person");
            }).ConfigureAwait(false);

            await foreach (var o in result)
            {
            }
            result.Dispose();
        }

        [Fact]
        public async Task Test_Views()
        {
            var cluster = _fixture.Cluster;
            var bucket = await cluster.BucketAsync("beer-sample").ConfigureAwait(false);

            var results = await bucket.ViewQueryAsync<object, object>("beer", "brewery_beers").ConfigureAwait(false);
            await foreach (var result in results)
            {
            }
        }

        [Fact]
        public async Task Test_WaitUntilReadyAsync()
        {
            var cluster = _fixture.Cluster;
            await cluster.WaitUntilReadyAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        [Theory]
        [InlineData(ServiceType.KeyValue)]
        [InlineData(ServiceType.Views)]
        [InlineData(ServiceType.KeyValue, ServiceType.Views, ServiceType.Analytics, ServiceType.Query)]
        public async Task Test_WaitUntilReadyAsync_with_options(params ServiceType[] serviceTypes)
        {
            var cluster = _fixture.Cluster;
            var options = new WaitUntilReadyOptions()
            {
                CancellationTokenValue = CancellationToken.None,
                ServiceTypesValue = serviceTypes
            };

            await cluster.WaitUntilReadyAsync(TimeSpan.FromSeconds(10), options).ConfigureAwait(false);
        }
    }
}
