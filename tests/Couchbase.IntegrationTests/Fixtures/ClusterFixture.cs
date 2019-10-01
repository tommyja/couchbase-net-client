using System;
using System.Threading.Tasks;
using Couchbase.KeyValue;
using Microsoft.Extensions.Configuration;

namespace Couchbase.IntegrationTests.Fixtures
{
    public class ClusterFixture : IDisposable
    {
        private readonly TestSettings _settings;
        private bool _bucketOpened;

        public ClusterOptions ClusterOptions { get; }

        public ICluster Cluster { get; }

        public ClusterFixture()
        {
            _settings = GetSettings();
            ClusterOptions = GetClusterOptions();

            Cluster = Couchbase.Cluster.Connect(
                _settings.ConnectionString,
                builder => builder.AddJsonFile("config.json")
            );
        }

        public async ValueTask<ICluster> GetCluster()
        {
            if (_bucketOpened)
            {
                return Cluster;
            }

            await GetDefaultBucket();
            return Cluster;
        }

        public async Task<IBucket> GetDefaultBucket()
        {
            var bucket = await Cluster.BucketAsync(_settings.BucketName);

            _bucketOpened = true;

            return bucket;
        }

        public async Task<ICollection> GetDefaultCollection()
        {
            var bucket = await GetDefaultBucket();
            return bucket.DefaultCollection();
        }

        private static TestSettings GetSettings()
        {
            return new ConfigurationBuilder()
                .AddJsonFile("config.json")
                .Build()
                .GetSection("testSettings")
                .Get<TestSettings>();
        }

        private static ClusterOptions GetClusterOptions()
        {
            return new ConfigurationBuilder()
                .AddJsonFile("config.json")
                .Build()
                .GetSection("couchbase")
                .Get<ClusterOptions>();
        }

        public void Dispose()
        {
            Cluster?.Dispose();
        }
    }
}
