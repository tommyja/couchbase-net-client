using System;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core;
using Couchbase.Core.Bootstrapping;
using Couchbase.Core.Configuration.Server;
using Couchbase.Core.DI;
using Couchbase.Core.Exceptions.KeyValue;
using Couchbase.Core.IO.HTTP;
using Couchbase.Core.IO.Operations;
using Couchbase.Core.Logging;
using Couchbase.Core.Retry;
using Couchbase.Core.Sharding;
using Couchbase.KeyValue;
using Couchbase.Management.Collections;
using Couchbase.Management.Views;
using Couchbase.Views;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

#nullable enable

namespace Couchbase
{
    internal class CouchbaseBucket : BucketBase
    {
        private readonly IVBucketKeyMapperFactory _vBucketKeyMapperFactory;
        private readonly Lazy<IViewClient> _viewClientLazy;
        private readonly Lazy<IViewIndexManager> _viewManagerLazy;
        private readonly Lazy<ICouchbaseCollectionManager> _collectionManagerLazy;

        internal CouchbaseBucket(string name, ClusterContext context, IScopeFactory scopeFactory, IRetryOrchestrator retryOrchestrator,
            IVBucketKeyMapperFactory vBucketKeyMapperFactory, ILogger<CouchbaseBucket> logger, IRedactor redactor, IBootstrapperFactory bootstrapperFactory)
            : base(name, context, scopeFactory, retryOrchestrator, logger, redactor, bootstrapperFactory)
        {
            _vBucketKeyMapperFactory = vBucketKeyMapperFactory ?? throw new ArgumentNullException(nameof(vBucketKeyMapperFactory));

            _viewClientLazy = new Lazy<IViewClient>(() =>
                context.ServiceProvider.GetRequiredService<IViewClient>()
            );
            _viewManagerLazy = new Lazy<IViewIndexManager>(() =>
                new ViewIndexManager(name,
                    context.ServiceProvider.GetRequiredService<IServiceUriProvider>(),
                    context.ServiceProvider.GetRequiredService<CouchbaseHttpClient>(),
                    context.ServiceProvider.GetRequiredService<ILogger<ViewIndexManager>>(),
                    redactor));

            _collectionManagerLazy = new Lazy<ICouchbaseCollectionManager>(() =>
                new CollectionManager(name,
                    context.ServiceProvider.GetRequiredService<IServiceUriProvider>(),
                    context.ServiceProvider.GetRequiredService<CouchbaseHttpClient>(),
                    context.ServiceProvider.GetRequiredService<ILogger<CollectionManager>>(),
                    redactor)
            );
        }

        public override IScope this[string scopeName]
        {
            get
            {
                Logger.LogDebug("Fetching scope {scopeName}", Redactor.UserData(scopeName));

                if (Scopes.TryGetValue(scopeName, out var scope))
                {
                    return scope;
                }

                throw new ScopeNotFoundException(scopeName);
            }
        }

        public override IViewIndexManager ViewIndexes => _viewManagerLazy.Value;

        /// <summary>
        /// The Collection Management API.
        /// </summary>
        /// <remarks>Volatile</remarks>
        public override ICouchbaseCollectionManager Collections => _collectionManagerLazy.Value;

        public override async Task ConfigUpdatedAsync(BucketConfig config)
        {
            if (config.Name == Name && (BucketConfig == null || config.Rev > BucketConfig.Rev))
            {
                Logger.LogDebug("Processing cluster map for revision {revision} on {bucketName}", config.Rev, Name);
                Logger.LogDebug(JsonConvert.SerializeObject(BucketConfig));
                BucketConfig = config;
                if (BucketConfig.VBucketMapChanged)
                {
                    Logger.LogDebug(LoggingEvents.ConfigEvent, "Updating VB key mapper for revision {revision}", config.Rev);
                    KeyMapper = await _vBucketKeyMapperFactory.CreateAsync(BucketConfig).ConfigureAwait(false);
                }

                if (BucketConfig.ClusterNodesChanged)
                {
                    Logger.LogDebug(LoggingEvents.ConfigEvent, "Updating cluster nodes for revision {revision}", config.Rev);
                    await Context.ProcessClusterMapAsync(this, BucketConfig).ConfigureAwait(false);
                    var nodes = Context.GetNodes(Name);

                    //update the local nodes collection
                    lock (Nodes)
                    {
                        Nodes.Clear();
                        foreach (var clusterNode in nodes)
                        {
                            Nodes.Add(clusterNode);
                        }
                    }
                }
            }
        }

        //TODO move Uri storage to ClusterNode - IBucket owns BucketConfig though
        private Uri GetViewUri()
        {
            var clusterNode = Context.GetRandomNodeForService(ServiceType.Views, Name);
            if (clusterNode?.ViewsUri == null)
            {
                throw new ServiceMissingException("Views Service cannot be located.");
            }
            return clusterNode.ViewsUri;
        }

        /// <inheritdoc />
        public override async Task<IViewResult<TKey, TValue>> ViewQueryAsync<TKey, TValue>(string designDocument, string viewName, ViewOptions? options = null)
        {
            ThrowIfBootStrapFailed();

            options ??= new ViewOptions();
            // create old style query
            var query = new ViewQuery(GetViewUri().ToString())
            {
                UseSsl = Context.ClusterOptions.EffectiveEnableTls
            };

            //Normalize to new naming convention for public API RFC#51
            var staleState = StaleState.None;
            if (options.ScanConsistencyValue == ViewScanConsistency.RequestPlus)
            {
                staleState = StaleState.False;
            }
            if (options.ScanConsistencyValue == ViewScanConsistency.UpdateAfter)
            {
                staleState = StaleState.UpdateAfter;
            }
            if (options.ScanConsistencyValue == ViewScanConsistency.NotBounded)
            {
                staleState = StaleState.Ok;
            }

            query.Bucket(Name);
            query.From(designDocument, viewName);
            query.Stale(staleState);
            query.Limit(options.LimitValue);
            query.Skip(options.SkipValue);
            query.StartKey(options.StartKeyValue);
            query.StartKeyDocId(options.StartKeyDocIdValue);
            query.EndKey(options.EndKeyValue);
            query.EndKeyDocId(options.EndKeyDocIdValue);
            query.InclusiveEnd(options.InclusiveEndValue);
            query.Group(options.GroupValue);
            query.GroupLevel(options.GroupLevelValue);
            query.Key(options.KeyValue);
            query.Keys(options.KeysValue);
            query.Reduce(options.ReduceValue);
            query.Development(options.DevelopmentValue);
            query.Debug(options.DebugValue);
            query.Namespace(options.NamespaceValue);
            query.OnError(options.OnErrorValue == ViewErrorMode.Stop);
            query.Timeout = options.TimeoutValue ?? Context.ClusterOptions.ViewTimeout;
            query.Serializer = options.SerializerValue;

            if (options.ViewOrderingValue == ViewOrdering.Decesending)
            {
                query.Desc();
            }
            else
            {
                query.Asc();
            }

            if (options.FullSetValue.HasValue && options.FullSetValue.Value)
            {
                query.FullSet();
            }

            foreach (var kvp in options.RawParameters)
            {
                query.Raw(kvp.Key, kvp.Value);
            }

            async Task<IViewResult<TKey, TValue>> Func()
            {
                var client1 = _viewClientLazy.Value;
                return await client1.ExecuteAsync<TKey, TValue>(query).ConfigureAwait(false);
            }

            return await RetryOrchestrator.RetryAsync(Func, query).ConfigureAwait(false);
        }

        internal override async Task SendAsync(IOperation op, CancellationToken token = default, TimeSpan? timeout = null)
        {
            if (KeyMapper == null)
            {
                throw new InvalidOperationException($"Bucket {Name} is not bootstrapped.");
            }

            var vBucket = (VBucket) KeyMapper.MapKey(op.Key);
            var endPoint = op.ReplicaIdx > 0 ?
                vBucket.LocateReplica(op.ReplicaIdx) :
                vBucket.LocatePrimary();

            op.VBucketId = vBucket.Index;

            if (Nodes.TryGet(endPoint!, out var clusterNode))
            {
                try
                {
                    await clusterNode.SendAsync(op, token, timeout).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    if (e is CollectionOutdatedException)
                    {
                        Logger.LogInformation("Updating stale manifest for collection and retrying.", e);
                        await RefreshCollectionId(op, clusterNode).ConfigureAwait(false);
                        await clusterNode.SendAsync(op, token, timeout).ConfigureAwait(false);
                    }
                    else
                    {
                        throw;//propagate up
                    }
                }
            }
            else
            {
                if (endPoint != null)
                {
                    throw new NodeNotAvailableException(
                        $"Cannot find a Couchbase Server node for {endPoint}.");
                }
                else
                {
                    throw new NullReferenceException($"IPEndPoint is null for key {op.Key}.");
                }
            }
        }

        private async Task RefreshCollectionId(IOperation op, IClusterNode node)
        {
            var scope = Scope(op.SName);
            var collection = (CouchbaseCollection)scope.Collection(op.CName);
            var newCid = await node.GetCid($"{op.SName}.{op.CName}").ConfigureAwait(false);
            collection.Cid = newCid;
            op.Cid = collection.Cid;
        }

        internal override async Task BootstrapAsync(IClusterNode node)
        {
            try
            {
                await node.SelectBucketAsync(this).ConfigureAwait(false);

                if (Context.SupportsCollections)
                {
                    Manifest = await node.GetManifest().ConfigureAwait(false);
                }

                //we still need to add a default collection
                LoadManifest();

                BucketConfig = await node.GetClusterMap().ConfigureAwait(false);
                BucketConfig.NetworkResolution = Context.ClusterOptions.NetworkResolution;
                KeyMapper = await _vBucketKeyMapperFactory.CreateAsync(BucketConfig).ConfigureAwait(false);

                Nodes.Add(node);
                await Context.ProcessClusterMapAsync(this, BucketConfig).ConfigureAwait(false);
                ClearErrors();
            }
            catch (Exception e)
            {
                if (e is CouchbaseException ce)
                {
                    if (ce.Context is KeyValueErrorContext ctx
                        && ctx.Status == ResponseStatus.NotSupported)
                    {
                        throw new NotSupportedException();
                    }
                }
                CaptureException(e);
            }

            //this needs to be started after bootstrapping has been attempted
            Bootstrapper.Start(this);
        }

        private void ClearErrors()
        {
            ((IBootstrappable)this).DeferredExceptions.Clear();
        }
    }
}
