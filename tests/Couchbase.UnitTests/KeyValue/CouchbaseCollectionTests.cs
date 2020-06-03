using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core;
using Couchbase.Core.Bootstrapping;
using Couchbase.Core.CircuitBreakers;
using Couchbase.Core.Configuration.Server;
using Couchbase.Core.DI;
using Couchbase.Core.Exceptions;
using Couchbase.Core.Exceptions.KeyValue;
using Couchbase.Core.IO;
using Couchbase.Core.IO.Connections;
using Couchbase.Core.IO.Operations;
using Couchbase.Core.IO.Transcoders;
using Couchbase.Core.Logging;
using Couchbase.Core.Retry;
using Couchbase.KeyValue;
using Couchbase.Management.Buckets;
using Couchbase.Management.Collections;
using Couchbase.Management.Views;
using Couchbase.Query;
using Couchbase.Views;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Couchbase.UnitTests.KeyValue
{
    public class CouchbaseCollectionTests
    {
        [Fact]
        public void Get_Timed_Out_Throw_TimeoutException()
        {
            var collection = CreateTestCollection();

            Assert.ThrowsAsync<TimeoutException>(async () => await collection.GetAsync("key", options =>
            {
                options.Timeout(TimeSpan.FromMilliseconds(1d));
            }));
        }

        [Fact]
        public async Task SubDoc_More_Than_One_XAttr_Throws_ArgumentException()
        {
            var collection = CreateTestCollection();

            await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                await collection.LookupInAsync("docId", builder =>
                {
                    builder.Get("doc.path", isXattr: true);
                    builder.Count("path", isXattr: true);
                }, new LookupInOptions().Timeout(TimeSpan.FromHours(1)));
            });
        }

        [Theory]
        //specific key value errors
        [InlineData(ResponseStatus.KeyNotFound, typeof(DocumentNotFoundException))]
        [InlineData(ResponseStatus.KeyExists, typeof(DocumentExistsException))]
        [InlineData(ResponseStatus.ValueTooLarge, typeof(ValueToolargeException))]
        [InlineData(ResponseStatus.InvalidArguments, typeof(InvalidArgumentException))]
        [InlineData(ResponseStatus.TemporaryFailure, typeof(TemporaryFailureException))]
        [InlineData(ResponseStatus.OperationTimeout, typeof(TimeoutException))]
        [InlineData(ResponseStatus.Locked, typeof(DocumentLockedException))]
        //durability errors
        [InlineData(ResponseStatus.DurabilityInvalidLevel, typeof(DurabilityLevelNotAvailableException))]
        [InlineData(ResponseStatus.DurabilityImpossible, typeof(DurabilityImpossibleException))]
        [InlineData(ResponseStatus.SyncWriteInProgress, typeof(DurableWriteInProgressException))]
        [InlineData(ResponseStatus.SyncWriteAmbiguous, typeof(DurabilityAmbiguousException))]
        //auth errors
        [InlineData(ResponseStatus.AuthenticationError, typeof(AuthenticationFailureException))]
        //internal errors
        //[InlineData(ResponseStatus.InternalError, typeof(InternalErrorException))]
        [InlineData(ResponseStatus.Eaccess, typeof(AuthenticationFailureException))]
        //[InlineData(ResponseStatus.Rollback, typeof(InternalErrorException))]
        //[InlineData(ResponseStatus.VBucketBelongsToAnotherServer, typeof(InternalErrorException))]
        [InlineData(ResponseStatus.AuthenticationContinue, typeof(AuthenticationFailureException))]
        [InlineData(ResponseStatus.AuthStale, typeof(AuthenticationFailureException))]
        //generic key-value errors
        [InlineData(ResponseStatus.InvalidRange, typeof(DeltaInvalidException))]
        //[InlineData(ResponseStatus.ItemNotStored, typeof(KeyValueException))]
       // [InlineData(ResponseStatus.IncrDecrOnNonNumericValue, typeof(KeyValueException))]
        //sub doc errors
        [InlineData(ResponseStatus.SubDocPathNotFound, typeof(PathNotFoundException))]
        [InlineData(ResponseStatus.SubDocPathMismatch, typeof(PathMismatchException))]
        [InlineData(ResponseStatus.SubDocPathInvalid, typeof(PathInvalidException))]
        [InlineData(ResponseStatus.SubDocPathTooBig, typeof(PathTooDeepException))]
        [InlineData(ResponseStatus.SubDocDocTooDeep, typeof(DocumentTooDeepException))]
        [InlineData(ResponseStatus.SubDocCannotInsert, typeof(ValueNotJsonException))]
        [InlineData(ResponseStatus.SubDocDocNotJson, typeof(DocumentNotJsonException))]
        [InlineData(ResponseStatus.SubDocNumRange, typeof(NumberTooBigException))]
        [InlineData( ResponseStatus.SubDocDeltaRange, typeof(DeltaInvalidException))]
        [InlineData(ResponseStatus.SubDocPathExists, typeof(PathExistsException))]
        [InlineData( ResponseStatus.SubDocValueTooDeep, typeof(ValueTooDeepException))]
        [InlineData(ResponseStatus.SubDocInvalidCombo, typeof(InvalidArgumentException))]
        //[InlineData(ResponseStatus.SubDocMultiPathFailure, typeof(InternalErrorException))]
        [InlineData(ResponseStatus.SubDocXattrInvalidFlagCombo, typeof(XattrException))]
        [InlineData(ResponseStatus.SubDocXattrInvalidKeyCombo, typeof(XattrException))]
        [InlineData( ResponseStatus.SubdocXattrUnknownMacro, typeof(XattrException))]
        [InlineData( ResponseStatus.SubdocXattrUnknownVattr, typeof(XattrException))]
        [InlineData( ResponseStatus.SubdocXattrCantModifyVattr, typeof(XattrException))]
        //[InlineData(ResponseStatus.SubdocMultiPathFailureDeleted, typeof(InternalErrorException))]
        [InlineData( ResponseStatus.SubdocInvalidXattrOrder, typeof(XattrException))]
        public async Task Get_Fails_Throw_KeyValueException(ResponseStatus responseStatus, Type exceptionType)
        {
            var collection = CreateTestCollection(responseStatus);

            try
            {
                using (await collection.GetAsync("key"))
                {
                }
            }
            catch (Exception e)
            {
                Assert.IsType(exceptionType, e);
            }
        }

        [Fact]
        public void Set_Factory_Test()
        {
            var collection = CreateTestCollection();

            var set = collection.Set<dynamic>("theDocId");
            Assert.NotNull(set);
        }

        [Fact]
        public async Task MutationOperations_Pass_BucketName_To_MutationToken()
        {
            var collection = CreateTestCollection();

            // BucketName asserted in Mock.
            var mutationTasks = new Task[]
            {
                collection.AppendAsync("theDocId", new byte[] {0xff}),
                collection.DecrementAsync("theDocId"),
                collection.IncrementAsync("theDocId"),
                collection.GetAndTouchAsync("theDocId", TimeSpan.FromSeconds(10)),
                collection.PrependAsync("theDocId", new byte[] { 0x00 }),
                collection.UpsertAsync<dynamic>("theDocId", new {foo = "bar"})
            };

            await Task.WhenAll(mutationTasks);
        }

        [Fact]
        public void Queue_Factory_Test()
        {
            var collection = CreateTestCollection();

            var queue = collection.Queue<dynamic>("theDocId");
            Assert.NotNull(queue);
        }

        [Fact]
        public void List_Factory_Test()
        {
            var collection = CreateTestCollection();

            var list = collection.List<dynamic>("theDocId");
            Assert.NotNull(list);
        }

        [Fact]
        public void Dictionary_Factory_Test()
        {
            var collection = CreateTestCollection();

            var dict = collection.Dictionary<dynamic>("theDocId");
            Assert.NotNull(dict);
        }

        [Fact]
        public void GetAsync_Allows_No_GetOptions()
        {
            var collection = CreateTestCollection();

            collection.GetAsync("key").GetAwaiter().GetResult();
        }

        internal class FakeBucket : BucketBase
        {
            internal const string BucketName = "fake";
            private readonly Queue<ResponseStatus> _statuses = new Queue<ResponseStatus>();

            public FakeBucket(params ResponseStatus[] statuses)
                : base(BucketName, new ClusterContext(), new Mock<IScopeFactory>().Object,
                    new Mock<IRetryOrchestrator>().Object, new Mock<ILogger>().Object, new Mock<IRedactor>().Object,
                    new Mock<IBootstrapperFactory>().Object)
            {
                foreach (var responseStatus in statuses) _statuses.Enqueue(responseStatus);
            }

            public override IViewIndexManager ViewIndexes => throw new NotImplementedException();

            public override ICouchbaseCollectionManager Collections => throw new NotImplementedException();

            internal override async Task SendAsync(IOperation op, CancellationToken token = default, TimeSpan? timeout = null)
            {
                var mockConnectionPool = new Mock<IConnectionPool>();

                var mockConnectionPoolFactory = new Mock<IConnectionPoolFactory>();
                mockConnectionPoolFactory
                    .Setup(m => m.Create(It.IsAny<ClusterNode>()))
                    .Returns(mockConnectionPool.Object);

                var clusterNode = new ClusterNode(new ClusterContext(), mockConnectionPoolFactory.Object,
                    new Mock<ILogger<ClusterNode>>().Object, new Mock<ITypeTranscoder>().Object,
                    new Mock<ICircuitBreaker>().Object,
                    new Mock<ISaslMechanismFactory>().Object,
                    new Mock<IRedactor>().Object,
                    new IPEndPoint(IPAddress.Parse("127.0.0.1"), 11210),
                    BucketType.Couchbase);

                await clusterNode.ExecuteOp(op, token, timeout);

                if (_statuses.TryDequeue(out ResponseStatus status))
                {
                    (op as OperationBase)?.HandleOperationCompleted(null, status);
                }
                else
                {
                    throw new InvalidOperationException();
                }
            }

            public override IScope this[string name] => throw new NotImplementedException();

            public override Task<IViewResult<TKey, TValue>> ViewQueryAsync<TKey, TValue>(string designDocument, string viewName, ViewOptions options = null)
            {
                throw new NotImplementedException();
            }

            internal override Task BootstrapAsync(IClusterNode bootstrapNodes)
            {
                throw new NotImplementedException();
            }

            public override Task ConfigUpdatedAsync(BucketConfig config)
            {
                throw new NotImplementedException();
            }
        }

        private static CouchbaseCollection CreateTestCollection(ResponseStatus getResult = ResponseStatus.Success)
        {
            var mockBucket = new Mock<FakeBucket>();
            mockBucket
                .Setup(m => m.RetryAsync(
                    It.Is<IOperation>(p => p.OpCode == OpCode.MultiLookup),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<TimeSpan>()))
                .Returns((IOperation operation, CancellationToken cancellationToken, TimeSpan timeout) =>
                {
                    operation.Header = new OperationHeader
                    {
                        Status = getResult
                    };

                    return Task.CompletedTask;
                });

            mockBucket.Setup(m => m.SendAsync(
                It.IsAny<IOperation>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<TimeSpan?>()))
                .Returns((IOperation operation, CancellationToken cancellationToken, TimeSpan timeout) =>
                {
                    operation.Header = new OperationHeader
                    {
                        Status = getResult
                    };

                    Assert.Equal(FakeBucket.BucketName, operation.BucketName);

                    return Task.CompletedTask;
                });

            return new CouchbaseCollection(mockBucket.Object, new LegacyTranscoder(),
                new Mock<ILogger<CouchbaseCollection>>().Object, new Mock<ILogger<GetResult>>().Object,
                new Mock<IRedactor>().Object,
                null, CouchbaseCollection.DefaultCollectionName, Mock.Of<IScope>());
        }
    }
}
