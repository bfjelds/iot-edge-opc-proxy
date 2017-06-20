// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy.Provider {
    using Proxy;
    using System;
    using System.Net.WebSockets;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Dataflow;

    /// <summary>
    /// Specialized implementation of a websocket based message stream
    /// </summary>
    internal class WebSocketMessageConnection : IConnection {

        /// <summary>
        /// Stream open completion source
        /// </summary>
        internal TaskCompletionSource<bool> Tcs { get; private set; } =
            new TaskCompletionSource<bool>();

        /// <summary>
        /// Stream reference
        /// </summary>
        internal Reference StreamId { get; private set; }

        /// <summary>
        /// Whether we were closed
        /// </summary>
        public bool Connected { get; set; } = false;

        /// <summary>
        /// Connection string for connection
        /// </summary>
        public ConnectionString ConnectionString { get; private set; }

        /// <summary>
        /// Never polled
        /// </summary>
        public bool IsPolled { get; } = false;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="provider"></param>
        /// <param name="streamId"></param>
        /// <param name="connectionString"></param>
        public WebSocketMessageConnection(WebsocketProvider provider, Reference streamId,
            ConnectionString connectionString) {
            _provider = provider;
            StreamId = streamId;
            ConnectionString = connectionString;
        }

        /// <summary>
        /// Accept this stream
        /// </summary>
        /// <param name="ct"></param>
        /// <returns></returns>
        public Task OpenAsync(IMessageStream stream, CancellationToken ct) {
            ct.Register(() => {
                Tcs.TrySetCanceled();
            });
            _stream = stream;
            _stream.SendBlock.LinkTo(new ActionBlock<Message>(async (message) => {
                try {
                    await _codec.WriteAsync(message, ct).ConfigureAwait(false);
                    await _codec.Stream.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) {
                    throw ProxyEventSource.Log.Rethrow(e, this);
                }
            }, new ExecutionDataflowBlockOptions {
                MaxDegreeOfParallelism = 1,
                CancellationToken = _open.Token,
            }));
            return Tcs.Task;
        }

        /// <summary>
        /// Close stream
        /// </summary>
        /// <returns></returns>
        public async Task CloseAsync() {
            // Remove ourselves from the listener...
            _provider._connectionMap.TryRemove(StreamId, out WebSocketMessageConnection stream);

            // Set close state
            _open.Cancel();

            // Fail any in progress open 
            Tcs.TrySetException(new SocketException(SocketError.Closed));

            ProxyEventSource.Log.StreamClosing(this, _codec.Stream);
            if (_producerTask != null) {
                try {
                    await _producerTask.ConfigureAwait(false);
                }
                catch { }
            }
            _producerTask = null;
        }

        /// <summary>
        /// Receive producer, reads messages one by one from websocket and 
        /// notifies consumers by completing queued completion sources.
        /// </summary>
        /// <returns></returns>
        private async Task ReceiveProducerAsync() {
            Connected = true;
            ProxyEventSource.Log.StreamOpened(this, _codec.Stream);
            try {
                while (true) {
                    try {
                        // Read message and send to source block
                        var message = await _codec.ReadAsync<Message>(_open.Token);
                        await _stream.ReceiveBlock.SendAsync(message, _open.Token);

                        if (message.TypeId == MessageContent.Close) {
                            // Remote side closed, close the stream
                            _open.Cancel();
                            _stream.ReceiveBlock.Complete();
                            break;
                        }
                    }
                    catch (Exception e) {
                        ProxyEventSource.Log.StreamException(this, _codec.Stream, e);
                        _stream.ReceiveBlock.Fault(e);
                        break;
                    }
                }
            }
            catch { }
            try {
                // ... then gracefully close
                await _codec.Stream.CloseAsync(new CancellationTokenSource(
                    _closeTimeout).Token).ConfigureAwait(false);
            }
            catch { }
            try {
                _codec.Stream.Dispose();
            }
            catch { }
            ProxyEventSource.Log.StreamClosed(this, _codec.Stream);
            _codec.Stream = null;
            Connected = false;
        }

        /// <summary>
        /// Connect the stream to a accepted stream instance and start the producer
        /// </summary>
        /// <param name="stream"></param>
        internal bool TryConnect(WebSocket webSocket) {
            if (_open.IsCancellationRequested) {
                // Stream closed, but proxy tries to connect, reject
                return false;
            }

            _codec.Stream = new WebSocketStream(webSocket);
            // Start producing
            _producerTask = Task.Run(async () => {
                await ReceiveProducerAsync();
            }, _open.Token);

            Tcs.TrySetResult(true);
            return true;
        }

        private MsgPackStream<WebSocketStream> _codec =
            new MsgPackStream<WebSocketStream>();
        private CancellationTokenSource _open = new CancellationTokenSource();
        private Task _producerTask;
        private IMessageStream _stream;
        private WebsocketProvider _provider;
        private static readonly TimeSpan _closeTimeout = TimeSpan.FromSeconds(3);
    }
}

