using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Couchbase.Core.Configuration.Server;
using Couchbase.Core.DI;
using Couchbase.Core.IO.HTTP;
using Couchbase.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

#nullable enable

namespace Couchbase.Core.Version
{
    /// <summary>
    /// Default implementation of <see cref="IClusterVersionProvider"/>.
    /// </summary>
    internal class ClusterVersionProvider : IClusterVersionProvider
    {
        private readonly ClusterContext _clusterContext;
        private readonly ILogger<ClusterVersionProvider> _logger;

        private ClusterVersion? _cachedVersion;

        public ClusterVersionProvider(ClusterContext clusterContext, ILogger<ClusterVersionProvider> logger)
        {
            _clusterContext = clusterContext ?? throw new ArgumentNullException(nameof(clusterContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async ValueTask<ClusterVersion?> GetVersionAsync()
        {
            var version = _cachedVersion;
            if (version != null)
            {
                return version;
            }

            version = await GetVersionAsync(_clusterContext.Nodes.Select(p => p.ManagementUri).Distinct(),
                _clusterContext.ServiceProvider.GetRequiredService<CouchbaseHttpClient>()).ConfigureAwait(false);

            if (version != null)
            {
                _cachedVersion = version;
            }

            return version;
        }

        /// <inheritdoc />
        public void ClearCache()
        {
            _cachedVersion = null;
        }

        private async Task<ClusterVersion?> GetVersionAsync(IEnumerable<Uri> servers, CouchbaseHttpClient httpClient)
        {
            if (servers == null)
            {
                throw new ArgumentNullException(nameof(servers));
            }

            foreach (var server in servers.ToList().Shuffle())
            {
                try
                {
                    _logger.LogTrace("Getting cluster version from {server}", server);

                    var config = await DownloadConfigAsync(httpClient, server).ConfigureAwait(false);
                    if (config != null && config.Nodes != null)
                    {
                        ClusterVersion? compatibilityVersion = null;
                        foreach (var node in config.Nodes)
                        {
                            if (ClusterVersion.TryParse(node.Version, out ClusterVersion version) &&
                                (compatibilityVersion == null || version < compatibilityVersion))
                            {
                                compatibilityVersion = version;
                            }
                        }

                        if (compatibilityVersion != null)
                        {
                            return compatibilityVersion;
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Unable to load config from {server}", server);
                }
            }

            // No version information could be loaded from any node
            _logger.LogDebug("Unable to get cluster version");
            return null;
        }

        protected virtual async Task<Pools> DownloadConfigAsync(HttpClient httpClient, Uri server)
        {
            try
            {
                var uri = new Uri(server, "/pools/default");

                var response = await httpClient.GetAsync(uri).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                return JsonConvert.DeserializeObject<Pools>(responseBody);
            }
            catch (AggregateException ex)
            {
                // Unwrap the aggregate exception
                throw new HttpRequestException(ex.InnerException?.Message, ex.InnerException);
            }
        }

        internal sealed class Pools
        {
            [JsonProperty("nodes")]
            public Node[]? Nodes { get; set; }
        }
    }
}
