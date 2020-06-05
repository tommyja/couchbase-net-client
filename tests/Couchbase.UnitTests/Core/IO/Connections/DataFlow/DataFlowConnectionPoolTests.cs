using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core.IO.Connections;
using Couchbase.Core.IO.Connections.DataFlow;
using Couchbase.Core.IO.Operations;
using Couchbase.Core.Logging;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Couchbase.UnitTests.Core.IO.Connections.DataFlow
{
    public class DataFlowConnectionPoolTests
    {
        private readonly ITestOutputHelper _testOutput;
        private readonly IPEndPoint _ipEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9999);

        public DataFlowConnectionPoolTests(ITestOutputHelper testOutput)
        {
            _testOutput = testOutput;
        }

        #region InitializeAsync

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        public async Task InitializeAsync_MinimumSize_OpensThatNumber(int size)
        {
            // Arrange

            var connectionFactory = new Mock<IConnectionFactory>();
            connectionFactory
                .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new Mock<IConnection>().Object);

            var pool = CreatePool(connectionFactory: connectionFactory.Object);
            pool.MinimumSize = size;
            pool.MaximumSize = size;

            // Act

            await pool.InitializeAsync();

            // Assert

            connectionFactory.Verify(
                m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()),
                Times.Exactly(size));
        }

        #endregion

        #region SendAsync

        [Fact]
        public async Task SendAsync_SingleOp_IsSent()
        {
            // Arrange

            var tcs = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource(10000); // prevent wait forever
            cts.Token.Register(() => tcs.TrySetResult(false));  // set result to false on timeout
            var pool = CreatePool();
            await pool.InitializeAsync();

            var operation = new FakeOperation
            {
                SendStarted = _ => tcs.TrySetResult(true)
            };

            // Act

            await pool.SendAsync(operation);

            // Assert

            Assert.True(await tcs.Task, "Send was not started before timeout");
        }

        [Fact]
        public async Task SendAsync_SingleOpCancelledBeforeDequeued_ThrowsCancelledException()
        {
            // Arrange

            var connection = new Mock<IConnection>();
            var connectionFactory = new Mock<IConnectionFactory>();
            connection.Setup(m => m.IsDead).Returns(true);
            connectionFactory
                .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => connection.Object);

            var pool = CreatePool(connectionFactory: connectionFactory.Object);
            pool.MinimumSize = 1;
            pool.MaximumSize = 1;

            await pool.InitializeAsync();

            var operation = new FakeOperation();

            // Act

            var sendTask = pool.SendAsync(operation, new CancellationTokenSource(50).Token);
            await Task.WhenAny(Task.Delay(3000), operation.Completed);

            // Assert

            Assert.True(operation.Completed.IsCompleted);
            Assert.True(operation.Completed.IsCanceled);
        }

        [Fact]
        public async Task SendAsync_SingleConnection_NotSentSimultaneously()
        {
            // Arrange

            var pool = CreatePool();
            pool.MinimumSize = 1;
            pool.MaximumSize = 1;

            await pool.InitializeAsync();

            var lockObject = new object();
            var toSendCount = 10;
            var inProgressCount = 0;
            var maxInProgressCount = 0;
            var totalSentCount = 0;
            var tcs = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource(10000);
            cts.Token.Register(() => tcs.TrySetResult(false)); // set result to false on timeout

            void SendStarted(IConnection _)
            {
                lock (lockObject)
                {
                    inProgressCount++;
                    maxInProgressCount = Math.Max(maxInProgressCount, inProgressCount);
                }
            }

            void SendCompleted(IConnection _)
            {
                lock (lockObject)
                {
                    inProgressCount--;
                    totalSentCount++;
                    if (totalSentCount == toSendCount)
                        tcs.TrySetResult(true);
                }
            }

            var operations = Enumerable.Range(1, toSendCount)
                .Select(_ => new FakeOperation
                {
                    Delay = TimeSpan.FromMilliseconds(100),
                    SendStarted = SendStarted,
                    SendComplete = SendCompleted
                })
                .ToList();

            // Act

            var tasks = operations.Select(p => pool.SendAsync(p)).ToList();

            // Assert

            Assert.True(await tcs.Task, "All sends were not started before timeout");
            Assert.Equal(1, maxInProgressCount);
            Assert.Equal(0, inProgressCount);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        public async Task SendAsync_MultipleConnections_SentSimultaneously(int connections)
        {
            // Arrange

            var pool = CreatePool();
            pool.MinimumSize = connections;
            pool.MaximumSize = connections;

            await pool.InitializeAsync();

            var toSendCount = 10;
            var lockObject = new object();
            var inProgressCount = 0;
            var maxInProgressCount = 0;
            var totalSentCount = 0;
            var tcs = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource(10000); // prevent wait forever
            cts.Token.Register(() => tcs.TrySetResult(false));  // set result to false on timeout

            void SendStarted(IConnection _)
            {
                lock (lockObject)
                {
                    inProgressCount++;
                    maxInProgressCount = Math.Max(maxInProgressCount, inProgressCount);
                }
            }

            void SendCompleted(IConnection _)
            {
                lock (lockObject)
                {
                    inProgressCount--;
                    totalSentCount++;
                    if (totalSentCount == toSendCount)
                        tcs.TrySetResult(true);
                }
            }

            var operations = Enumerable.Range(1, toSendCount)
                .Select(_ => new FakeOperation
                {
                    Delay = TimeSpan.FromMilliseconds(100),
                    SendStarted = SendStarted,
                    SendComplete = SendCompleted
                })
                .ToList();

            // Act

            var tasks = operations.Select(p => pool.SendAsync(p)).ToList();

            // Assert

            Assert.True(await tcs.Task, "All sends were not started before timeout");
            Assert.Equal(connections, maxInProgressCount);
            Assert.Equal(0, inProgressCount);
        }

        [Fact]
        public async Task SendAsync_DeadConnection_ReplacesConnectionAndStillSends()
        {
            // Arrange

            var connectionCount = 0ul;
            var tcs = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource(10000); // prevent wait forever
            cts.Token.Register(() => tcs.TrySetResult(false));  // set result to false on timeout

            var connectionFactoryMock = new Mock<IConnectionFactory>();
            connectionFactoryMock
                .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    connectionCount++;

                    var connection = new Mock<IConnection>();
                    if (connectionCount == 1)
                    {
                        // First connection is dead
                        connection
                            .SetupGet(m => m.IsDead)
                            .Returns(true);
                    }

                    connection
                        .SetupGet(m => m.ConnectionId)
                        .Returns(connectionCount);

                    return connection.Object;
                });

            var pool = CreatePool(connectionFactory: connectionFactoryMock.Object);
            pool.MinimumSize = 1;
            pool.MaximumSize = 1;

            await pool.InitializeAsync();

            var operationConnectionId = 0ul;
            var operation = new FakeOperation
            {
                SendStarted = connection =>
                {
                    operationConnectionId = connection.ConnectionId;
                    tcs.TrySetResult(true);
                }
            };

            // Act

            await pool.SendAsync(operation);
            Assert.True(await tcs.Task, "Send was not started before timeout");

            // Assert

            Assert.Equal(2ul, connectionCount);
            Assert.Equal(2ul, operationConnectionId);
        }

        #endregion

        #region Dispose

        [Fact]
        public async Task Dispose_ClosesAllConnections()
        {
            // Arrange

            var connectionCount = 0ul;
            var disposed = new HashSet<ulong>();

            var connectionFactoryMock = new Mock<IConnectionFactory>();
            connectionFactoryMock
                .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var connectionId = ++connectionCount;

                    var connection = new Mock<IConnection>();
                    connection
                        .SetupGet(m => m.ConnectionId)
                        .Returns(connectionId);
                    connection
                        .Setup(m => m.Dispose())
                        .Callback(() => disposed.Add(connectionId));

                    return connection.Object;
                });

            var pool = CreatePool(connectionFactory: connectionFactoryMock.Object);
            pool.MinimumSize = 5;
            pool.MaximumSize = 5;

            await pool.InitializeAsync();

            // Act

            pool.Dispose();

            // Assert

            Assert.All(
                Enumerable.Range(1, 5),
                p => Assert.Contains((ulong) p, disposed));
        }

        #endregion

        #region Scale

        [Fact]
        public async Task Scale_Zero_DoesNothing()
        {
            // Arrange

            var connections = new ConcurrentBag<Mock<IConnection>>();

            var connectionFactory = new Mock<IConnectionFactory>();
            connectionFactory
                .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var connection = new Mock<IConnection>();
                    connections.Add(connection);
                    return connection.Object;
                });

            var pool = CreatePool(connectionFactory: connectionFactory.Object);
            pool.MinimumSize = 2;
            pool.MaximumSize = 5;

            await pool.InitializeAsync();

            // Act

            await pool.ScaleAsync(0);

            // Assert

            Assert.Equal(pool.MinimumSize, pool.Size);
            Assert.All(connections, p => p.Verify(m => m.CloseAsync(It.IsAny<TimeSpan>()), Times.Never));

            connectionFactory.Verify(
                m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()),
                Times.Exactly(pool.MinimumSize));
        }

        [Fact]
        public async Task Scale_Up_AddsConnections()
        {
            // Arrange

            var connections = new ConcurrentBag<Mock<IConnection>>();

            var connectionFactory = new Mock<IConnectionFactory>();
            connectionFactory
                .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var connection = new Mock<IConnection>();
                    connections.Add(connection);
                    return connection.Object;
                });

            var pool = CreatePool(connectionFactory: connectionFactory.Object);
            pool.MinimumSize = 2;
            pool.MaximumSize = 5;

            await pool.InitializeAsync();

            // Act

            await pool.ScaleAsync(2);

            // Assert

            Assert.Equal(4, pool.Size);
            Assert.All(connections, p => p.Verify(m => m.CloseAsync(It.IsAny<TimeSpan>()), Times.Never));

            connectionFactory.Verify(
                m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()),
                Times.Exactly(4));
        }

        [Fact]
        public async Task Scale_UpAtMax_DoesNothing()
        {
            // Arrange

            var connections = new ConcurrentBag<Mock<IConnection>>();

            var connectionFactory = new Mock<IConnectionFactory>();
            connectionFactory
                .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var connection = new Mock<IConnection>();
                    connections.Add(connection);
                    return connection.Object;
                });

            var pool = CreatePool(connectionFactory: connectionFactory.Object);
            pool.MinimumSize = 2;
            pool.MaximumSize = 2;

            await pool.InitializeAsync();

            // Act

            await pool.ScaleAsync(1);

            // Assert

            Assert.Equal(pool.MaximumSize, pool.Size);
            Assert.All(connections, p => p.Verify(m => m.CloseAsync(It.IsAny<TimeSpan>()), Times.Never));

            connectionFactory.Verify(
                m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()),
                Times.Exactly(pool.MaximumSize));
        }

        [Fact]
        public async Task Scale_UpMoreThanMax_ScalesToMax()
        {
            // Arrange

            var connections = new ConcurrentBag<Mock<IConnection>>();

            var connectionFactory = new Mock<IConnectionFactory>();
            connectionFactory
                .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var connection = new Mock<IConnection>();
                    connections.Add(connection);
                    return connection.Object;
                });

            var pool = CreatePool(connectionFactory: connectionFactory.Object);
            pool.MinimumSize = 2;
            pool.MaximumSize = 5;

            await pool.InitializeAsync();

            // Act

            await pool.ScaleAsync(4);

            // Assert

            Assert.Equal(pool.MaximumSize, pool.Size);
            Assert.All(connections, p => p.Verify(m => m.CloseAsync(It.IsAny<TimeSpan>()), Times.Never));

            connectionFactory.Verify(
                m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()),
                Times.Exactly(pool.MaximumSize));
        }

        [Fact]
        public async Task Scale_Down_DropsConnections()
        {
            // Arrange

            var connections = new ConcurrentBag<Mock<IConnection>>();

            var connectionFactory = new Mock<IConnectionFactory>();
            connectionFactory
                .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var connection = new Mock<IConnection>();
                    connections.Add(connection);
                    return connection.Object;
                });

            var pool = CreatePool(connectionFactory: connectionFactory.Object);
            pool.MinimumSize = 2;
            pool.MaximumSize = 5;

            await pool.InitializeAsync();
            await pool.ScaleAsync(3);

            // Act

            await pool.ScaleAsync(-2);

            // Assert

            Assert.Equal(3, pool.Size);

            var closedConnections = connections
                .Where(p => p.Invocations.Any(q => q.Method == typeof(IConnection).GetMethod("CloseAsync")))
                .Select(p => p.Object)
                .ToList();

            Assert.Equal(2, closedConnections.Count);
            Assert.All(closedConnections, p => Assert.DoesNotContain(p, pool.GetConnections()));

            connectionFactory.Verify(
                m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()),
                Times.Exactly(5));
        }

        [Fact]
        public async Task Scale_Down_DoesNotWaitForClose()
        {
            // Arrange

            var isInClose = false;
            var isClosed = false;

            var connectionFactory = new Mock<IConnectionFactory>();
            connectionFactory
                .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var connection = new Mock<IConnection>();
                    connection.Setup(m => m.CloseAsync(It.IsAny<TimeSpan>()))
                        .Callback(async (TimeSpan _) =>
                        {
                            isInClose = true;
                            await Task.Delay(TimeSpan.FromSeconds(1));
                            isClosed = true;
                        });
                    return connection.Object;
                });

            var pool = CreatePool(connectionFactory: connectionFactory.Object);
            pool.MinimumSize = 2;
            pool.MaximumSize = 5;

            await pool.InitializeAsync();
            await pool.ScaleAsync(1);

            // Act

            Assert.False(isInClose);
            Assert.False(isClosed);
            await pool.ScaleAsync(-1);

            // Assert

            Assert.True(isInClose);
            Assert.False(isClosed);
        }

        [Fact]
        public async Task Scale_Down_ClosesLongestIdleFirst()
        {
            // Arrange

            var connectionCount = 0;
            var connections = new ConcurrentBag<Mock<IConnection>>();

            var connectionFactory = new Mock<IConnectionFactory>();
            connectionFactory
                .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var connection = new Mock<IConnection>();

                    connectionCount++;
                    connection
                        .SetupGet(m => m.IdleTime)
                        .Returns(TimeSpan.FromMinutes(connectionCount));

                    connections.Add(connection);
                    return connection.Object;
                });

            var pool = CreatePool(connectionFactory: connectionFactory.Object);
            pool.MinimumSize = 2;
            pool.MaximumSize = 5;

            await pool.InitializeAsync();
            await pool.ScaleAsync(1);

            // Act

            await pool.ScaleAsync(-1);

            // Assert

            var closedConnections = connections
                .Where(p => p.Invocations.Any(q => q.Method == typeof(IConnection).GetMethod("CloseAsync")))
                .Select(p => p.Object);

            var closedConnection = Assert.Single(closedConnections);
            Assert.NotNull(closedConnection);

            Assert.Equal(TimeSpan.FromMinutes(3), closedConnection.IdleTime);
        }

        [Fact]
        public async Task Scale_DownAtMin_DoesNothing()
        {
            // Arrange

            var connections = new ConcurrentBag<Mock<IConnection>>();

            var connectionFactory = new Mock<IConnectionFactory>();
            connectionFactory
                .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var connection = new Mock<IConnection>();
                    connections.Add(connection);
                    return connection.Object;
                });

            var pool = CreatePool(connectionFactory: connectionFactory.Object);
            pool.MinimumSize = 2;
            pool.MaximumSize = 2;

            await pool.InitializeAsync();

            // Act

            await pool.ScaleAsync(-1);

            // Assert

            Assert.Equal(pool.MinimumSize, pool.Size);
            Assert.All(connections, p => p.Verify(m => m.CloseAsync(It.IsAny<TimeSpan>()), Times.Never));

            connectionFactory.Verify(
                m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()),
                Times.Exactly(pool.MinimumSize));
        }

        [Fact]
        public async Task Scale_DownMoreThanMin_ScalesToMin()
        {
            // Arrange

            var connections = new ConcurrentBag<Mock<IConnection>>();

            var connectionFactory = new Mock<IConnectionFactory>();
            connectionFactory
                .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var connection = new Mock<IConnection>();
                    connections.Add(connection);
                    return connection.Object;
                });

            var pool = CreatePool(connectionFactory: connectionFactory.Object);
            pool.MinimumSize = 2;
            pool.MaximumSize = 5;

            await pool.InitializeAsync();
            await pool.ScaleAsync(2);

            // Act

            await pool.ScaleAsync(-3);

            // Assert

            Assert.Equal(pool.MinimumSize, pool.Size);

            var closedConnections = connections
                .Where(p => p.Invocations.Any(q => q.Method == typeof(IConnection).GetMethod("CloseAsync")))
                .Select(p => p.Object)
                .ToList();

            Assert.Equal(2, closedConnections.Count);
            Assert.All(closedConnections, p => Assert.DoesNotContain(p, pool.GetConnections()));

            connectionFactory.Verify(
                m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()),
                Times.Exactly(4));
        }

        #endregion

        #region Helpers

        private DataFlowConnectionPool CreatePool(IConnectionInitializer connectionInitializer = null,
            IConnectionFactory connectionFactory = null)
        {
            if (connectionInitializer == null)
            {
                var connectionInitializerMock = new Mock<IConnectionInitializer>();
                connectionInitializerMock
                    .SetupGet(m => m.EndPoint)
                    .Returns(_ipEndPoint);

                connectionInitializer = connectionInitializerMock.Object;
            }

            if (connectionFactory == null)
            {
                var connectionFactoryMock = new Mock<IConnectionFactory>();
                connectionFactoryMock
                    .Setup(m => m.CreateAndConnectAsync(_ipEndPoint, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => new Mock<IConnection>().Object);

                connectionFactory = connectionFactoryMock.Object;
            }

            return new DataFlowConnectionPool(connectionInitializer, connectionFactory,
                new Mock<IConnectionPoolScaleController>().Object,
                new Mock<IRedactor>().Object,
                new Logger(_testOutput));
        }

        private class FakeOperation : OperationBase
        {
            public TimeSpan Delay { get; set; } = TimeSpan.Zero;

            public Action<IConnection> SendStarted { get; set; }

            public Action<IConnection> SendComplete { get; set; }

            public override OpCode OpCode => OpCode.NoOp;

            public override async Task SendAsync(IConnection connection, CancellationToken cancellationToken = default)
            {
                SendStarted?.Invoke(connection);

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay);
                }

                SendComplete?.Invoke(connection);
            }
        }

        private class Logger : ILogger<DataFlowConnectionPool>
        {
            private readonly ITestOutputHelper _testOutput;

            public Logger(ITestOutputHelper testOutput)
            {
                _testOutput = testOutput ?? throw new ArgumentNullException(nameof(testOutput));
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                _testOutput.WriteLine(formatter(state, exception));
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public IDisposable BeginScope<TState>(TState state)
            {
                return null;
            }
        }

        #endregion
    }
}
