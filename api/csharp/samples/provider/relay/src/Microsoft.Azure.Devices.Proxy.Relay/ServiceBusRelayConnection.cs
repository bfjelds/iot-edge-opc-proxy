// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy.Provider {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Dataflow;
    using Relay;

    /// <summary>
    /// Specialized implementation of relay based message stream
    /// </summary>
    internal class ServiceBusRelayConnection : IConnection, IMessageStream {

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
        /// <param name="relay"></param>
        /// <param name="streamId"></param>
        /// <param name="connectionString"></param>
        public ServiceBusRelayConnection(ServiceBusRelay relay, Reference streamId,
            ConnectionString connectionString) {
            _relay = relay;

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
        public Task CloseAsync() {
            Task pumps;
            lock (this) {
                // Set close state
                _open.Cancel();

                _receive.Complete();
                _send.Complete();

                // Remove ourselves from the listener...
                _relay._connectionMap.TryRemove(StreamId, out ServiceBusRelayConnection stream);

                // Fail any in progress open 
                if (!Tcs.Task.IsCompleted) {
                    Tcs.TrySetException(new SocketException(SocketError.Closed));
                }

                if (_pumps == null) {
                    return TaskEx.Completed;
                }

                pumps = _pumps;
                _pumps = null;
                return pumps;
            }
        }

        /// <summary>
        /// Send consumer, reading messages one by one from stream and writes
        /// to websocket stream.
        /// </summary>
        /// <returns></returns>
        private async Task SendConsumerAsync(MsgPackStream<HybridConnectionStream> codec) {
            while (!_open.IsCancellationRequested) {
                try {
                    if (_lastMessage == null) {
                        if (!await _send.OutputAvailableAsync(_open.Token)) {
                            // Pipeline closed, close the connection
                            _receive.Complete();
                            _open.Cancel();
                            break;
                        }
                        if (!_send.TryReceive(out _lastMessage)) {
                            continue;
                        }
                    }
#if !STREAM_FRAGMENT_BUG_FIXED
                    // Poor man's buffered stream.  
                    using (var mem = new MemoryStream()) {
                        _lastMessage.Encode(mem, CodecId.Mpack);
                        var buffered = mem.ToArray();
                        await codec.Stream.WriteAsync(
                            buffered, 0, buffered.Length, _open.Token).ConfigureAwait(false);
                    }
#else
                    await codec.WriteAsync(_lastMessage, _open.Token).ConfigureAwait(false);
#endif
                    await codec.Stream.FlushAsync(_open.Token).ConfigureAwait(false);
                    _lastMessage = null;
                }
                catch (OperationCanceledException) {
                    break;
                }
                catch (Exception e) {
                    ProxyEventSource.Log.StreamException(this, codec.Stream, e);
                    break;
                }
            }
        }

        /// <summary>
        /// Receive producer, reading messages one by one from relay and 
        /// writing to receive block.  Manages stream as well.
        /// </summary>
        /// <returns></returns>
        private async Task ReceiveProducerAsync(MsgPackStream<HybridConnectionStream> codec) {
            while (!_open.IsCancellationRequested) {
                try {
                    // Read message and send to source block
                    var message = await codec.ReadAsync<Message>(_open.Token);

                    if (!await _receive.SendAsync(message, _open.Token) ||
                            message.TypeId == MessageContent.Close) {
                        // Pipeline closed, close the connection
                        _send.Complete();
                        _open.Cancel();
                        break;
                    }
                }
                catch (OperationCanceledException) {
                    break;
                }
                catch (Exception e) {
                    ProxyEventSource.Log.StreamException(this, codec.Stream, e);
                    break;
                }
            }
        }

        /// <summary>
        /// Close stream when producers finish
        /// </summary>
        /// <param name="codec"></param>
        /// <returns></returns>
        private async Task CloseStreamAsync(MsgPackStream<HybridConnectionStream> codec, 
            bool parentHasFaulted) {
            ProxyEventSource.Log.StreamClosing(this, codec.Stream);
            if (!parentHasFaulted) {
                // try {
                //     if (_open.IsCancellationRequested) {
                //         // User is asking for a graceful close, shutdown properly
                //         await codec.Stream.ShutdownAsync(new CancellationTokenSource(
                //             ServiceBusRelay._closeTimeout).Token).ConfigureAwait(false);
                //     }
                // }
                // catch { }
                try {
                    // ... then gracefully close
                    await codec.Stream.CloseAsync(new CancellationTokenSource(
                        ServiceBusRelay._closeTimeout).Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) {
                }
                catch (Exception e) {
                    ProxyEventSource.Log.StreamException(this, codec.Stream, e);
                }
            }

            try {
                codec.Stream.Dispose();
            }
            catch (Exception e) {
                ProxyEventSource.Log.HandledExceptionAsInformation(codec.Stream, e);
            }
            ProxyEventSource.Log.StreamClosed(this, codec.Stream);
        }

        /// <summary>
        /// Attach the stream to a accepted stream instance and start to produce/consume
        /// </summary>
        /// <param name="stream"></param>
        internal bool TryConnect(HybridConnectionStream stream) {
            lock (this) {
                if (_open.IsCancellationRequested) {
                    // Stream closed, but proxy tries to connect, reject
                    return false;
                }

                // Start pumping...
                var codec = new MsgPackStream<HybridConnectionStream> { Stream = stream };

                var pumps = Task.WhenAll(new Task[] {
                    Task.Factory.StartNew(async () => 
                        await SendConsumerAsync(codec), _open.Token).Unwrap(),
                    Task.Factory.StartNew(async () => 
                        await ReceiveProducerAsync(codec), _open.Token).Unwrap()
                }).ContinueWith(t => CloseStreamAsync(codec, t.IsFaulted)).Unwrap();

                if (_pumps != null) {
                    // Reconnect
                    // _pumps = _pumps.ContinueWith(_ => pumps).Unwrap();
                    _pumps = pumps;
                }
                else {
                    // First connect
                    _pumps = pumps;
                    Tcs.TrySetResult(this);
                }
                ProxyEventSource.Log.StreamOpened(this, stream);
            }
            return true;
        }

        private readonly ServiceBusRelay _relay;
        private Task _pumps;
        private readonly BufferBlock<Message> _receive;
        private readonly BufferBlock<Message> _send;
        private Message _lastMessage;
        private readonly CancellationTokenSource _open = new CancellationTokenSource();
    }
}
