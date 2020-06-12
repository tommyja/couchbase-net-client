using System.Linq;
using System.Threading.Tasks;
using Couchbase.IntegrationTests.Fixtures;
using Couchbase.Query;
using Newtonsoft.Json;
using Xunit;
using Xunit.Abstractions;

namespace Couchbase.IntegrationTests.Services.Query
{
    public class QueryTests : IClassFixture<ClusterFixture>
    {
        private readonly ClusterFixture _fixture;
        private readonly ITestOutputHelper _testOutputHelper;

        public QueryTests(ClusterFixture fixture, ITestOutputHelper testOutputHelper)
        {
            _fixture = fixture;
            _testOutputHelper = testOutputHelper;
        }

        [Fact]
        public async Task Test_Query()
        {
            var cluster = await _fixture.GetCluster().ConfigureAwait(false);
            await cluster.QueryAsync<Poco>("SELECT default.* FROM `default` LIMIT 1;").ConfigureAwait(false);
        }

        [Fact]
        public async Task Test_Prepared()
        {
            var cluster = await _fixture.GetCluster().ConfigureAwait(false);

            // execute prepare first time
            var result = await cluster.QueryAsync<dynamic>("SELECT default.* FROM `default` LIMIT 1;",
                options => options.AdHoc(false)).ConfigureAwait(false);
            Assert.Equal(QueryStatus.Success, result.MetaData.Status);

            // should use prepared plan
            var preparedResult = await cluster.QueryAsync<dynamic>("SELECT default.* FROM `default` LIMIT 1;",
                options => options.AdHoc(false)).ConfigureAwait(false);
            Assert.Equal(QueryStatus.Success, preparedResult.MetaData.Status);
        }

        [Fact]
        public async Task Test_QueryWithSynchronousStream()
        {
            var cluster = await _fixture.GetCluster().ConfigureAwait(false);

            var result = await cluster.QueryAsync<dynamic>("SELECT default.* FROM `default` WHERE type=$name;",
                parameter =>
            {
                parameter.Parameter("name", "person");
            }).ConfigureAwait(false);

            // Non-streaming approach in C# 7
            foreach (var o in result.ToEnumerable())
            {
                _testOutputHelper.WriteLine(JsonConvert.SerializeObject(o, Formatting.None));
            }
            result.Dispose();
        }

        [Fact]
        public async Task Test_QueryWithAsyncStreamCSharp7()
        {
            var cluster = await _fixture.GetCluster().ConfigureAwait(false);

            var result = await cluster.QueryAsync<dynamic>("SELECT default.* FROM `default` WHERE type=$name;",
                parameter =>
                {
                    parameter.Parameter("name", "person");
                }).ConfigureAwait(false);

            // Async streaming approach in C# 7
            var enumerator = result.GetAsyncEnumerator();
            try
            {
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    _testOutputHelper.WriteLine(JsonConvert.SerializeObject(enumerator.Current, Formatting.None));
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            result.Dispose();
        }

        [Fact]
        public async Task Test_QueryWithAsyncStreamCSharp8()
        {
            var cluster = await _fixture.GetCluster().ConfigureAwait(false);

            var result = await cluster.QueryAsync<dynamic>("SELECT default.* FROM `default` WHERE type=$name;",
                parameter =>
                {
                    parameter.Parameter("name", "person");
                }).ConfigureAwait(false);

            await foreach (var o in result.ConfigureAwait(false))
            {
                _testOutputHelper.WriteLine(JsonConvert.SerializeObject(o, Formatting.None));
            }

            result.Dispose();
        }

        [Fact]
        public async Task Test_RawQuery()
        {
            var cluster = await _fixture.GetCluster().ConfigureAwait(false);
            using var result = await cluster.QueryAsync<string>("SELECT RAW name FROM `default` WHERE name IS VALUED LIMIT 1;").ConfigureAwait(false);

            var found = false;
            await foreach (var name in result.ConfigureAwait(false))
            {
                found = true;
                _testOutputHelper.WriteLine(name);
            }

            Assert.True(found);
        }

        // ReSharper disable UnusedType.Local
        private class Poco
        {
            public string Name { get; set; }
        }
        // ReSharper restore UnusedType.Local
    }
}
