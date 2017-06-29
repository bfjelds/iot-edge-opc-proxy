// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy.Provider {
    using System;
    using System.Net.WebSockets;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Dataflow;

    /// <summary>
    /// Specialized implementation of a websocket based message stream
    /// </summary>
    internal class WebSocketMessageConnection : IConnection, IMessageStream {

        /// <summary>
        /// Stream open completion source
        /// </summary>
        internal TaskCompletionSource<IMessageStream> Tcs { get; private set; } =
            new TaskCompletionSource<IMessageStream>();

        /// <summary>
        /// Stream reference
        /// </summary>
        internal Reference StreamId { get; private set; }

        /// <summary>
        /// Connection string for connection
        /// </summary>
        public ConnectionString ConnectionString { get; private set; }

        /// <summary>
        /// Never polled
        /// </summary>
        public bool IsPolled { get; } = false;

        /// <summary>
        /// Block to send to
        /// </summary>
        public ITargetBlock<Message> SendBlock { get; private set; }

        /// <summary>
        /// Block to receive from
        /// </summary>
        public ISourceBlock<Message> ReceiveBlock { get; private set; }

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

            ReceiveBlock = _receive = new BufferBlock<Message>(new DataflowBlockOptions {
                NameFormat = "Receive (in Stream) Id={1}",
                BoundedCapacity = 1,
                EnsureOrdered = true,
                MaxMessagesPerTask = DataflowBlockOptions.Unbounded,
                CancellationToken = _open.Token
            });

            SendBlock = _send = new BufferBlock<Message>(new DataflowBlockOptions {
                NameFormat = "Send (in Stream) Id={1}",
                BoundedCapacity = 1, 
                EnsureOrdered = true,
                MaxMessagesPerTask = DataflowBlockOptions.Unbounded,
                CancellationToken = _open.Token
            });
        }

        /// <summary>
        /// Accept this stream
        /// </summary>
        /// <param name="ct"></param>
        /// <returns></returns>
        public Task<IMessageStream> OpenAsync(CancellationToken ct) {
            ct.Register(() => {
                Tcs.TrySetCanceled();
            });
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

            if (_receivePump != null) {
                try {
                    await _receivePump.ConfigureAwait(false);
                }
                catch { }
            }
            _receivePump = null;
            if (_sendPump != null) {
                try {
                    await _sendPump.ConfigureAwait(false);
                }
                catch { }
            }
            _sendPump = null;
        }

        /// <summary>
        /// Send consumer, reading messages one by one from stream and writes
        /// to websocket stream.
        /// </summary>
        /// <returns></returns>
        private async Task SendConsumerAsync(MsgPackStream<WebSocketStream> codec) {
            try {
                while (true) {
                    try {
                        if (_lastMessage == null) {
                            _lastMessage = await _send.ReceiveAsync(_open.Token);
                        }
                        await codec.WriteAsync(_lastMessage, _open.Token).ConfigureAwait(false);
                        await codec.Stream.FlushAsync(_open.Token).ConfigureAwait(false);
                        _lastMessage = null;
                    }
                    catch (Exception e) {
                        ProxyEventSource.Log.StreamException(this, codec.Stream, e);
                        break;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Receive producer, reading messages one by one from relay and 
        /// writing to receive block.  Manages stream as well.
        /// </summary>
        /// <returns></returns>
        private async Task ReceiveProducerAsync(MsgPackStream<WebSocketStream> codec) {
            ProxyEventSource.Log.StreamOpened(this, codec.Stream);
            try {
                while (true) {
                    try {
                        // Read message and send to source block
                        var message = await codec.ReadAsync<Message>(_open.Token);
                        await _receive.SendAsync(message, _open.Token);

                        if (message.TypeId == MessageContent.Close) {
                            // Remote side closed, close the stream
                            _receive.Complete();
                            _send.Complete();
                            _open.Cancel();
                            break;
                        }
                    }
                    catch (Exception e) {
                        ProxyEventSource.Log.StreamException(this, codec.Stream, e);
                        break;
                    }
                }
            }
            catch { }
            try {
                // ... then gracefully close
                await codec.Stream.CloseAsync(new CancellationTokenSource(
                    _closeTimeout).Token).ConfigureAwait(false);
            }
            catch { }
            try {
                codec.Stream.Dispose();
            }
            catch { }
            ProxyEventSource.Log.StreamClosed(this, codec.Stream);
        }

        /// <summary>
        /// Attach the stream to a accepted stream instance and start to produce/consume
        /// </summary>
        /// <param name="stream"></param>
        internal bool TryConnect(WebSocket webSocket) {
            if (_open.IsCancellationRequested) {
                // Stream closed, but proxy tries to connect, reject
                return false;
            }
            lock (this) {
                var _codec = new MsgPackStream<WebSocketStream> {
					Stream = new WebSocketStream(webSocket) 
				};

                if (_sendPump != null) {
                    // Reconnect 
                    _sendPump = _sendPump.ContinueWith(t => SendConsumerAsync(_codec), _open.Token);
                }
                else {
                    _sendPump = Task.Factory.StartNew(async () => await SendConsumerAsync(_codec),
                        _open.Token).Unwrap();
                }

                if (_receivePump != null) {
                    // Reconnect 
                    _receivePump = _receivePump.ContinueWith(t => ReceiveProducerAsync(_codec), _open.Token);
                }
                else {
                    // First connect
                    _receivePump = Task.Factory.StartNew(async () => await ReceiveProducerAsync(_codec), 
                        _open.Token).Unwrap();
                    Tcs.TrySetResult(this);
                }
            }
            return true;
        }
        private WebsocketProvider _provider;
        private Task _receivePump;
        private readonly BufferBlock<Message> _receive;
        private Task _sendPump;
        private readonly BufferBlock<Message> _send;
        private Message _lastMessage;
        private readonly CancellationTokenSource _open = new CancellationTokenSource();
        private static readonly TimeSpan _closeTimeout = TimeSpan.FromSeconds(3);
    }
}

