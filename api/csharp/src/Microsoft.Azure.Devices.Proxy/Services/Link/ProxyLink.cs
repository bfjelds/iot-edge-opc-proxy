// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy {
    using System;
    using System.Linq;
    using System.Collections.Generic;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Dataflow;

    /// <summary>
    /// Proxy link represents a 1:1 link with a remote socket. Proxy
    /// Link is created via LinkRequest, and OpenRequest Handshake.
    /// </summary>
    internal sealed class ProxyLink : IProxyLink {

        /// <summary>
        /// Remote link id
        /// </summary>
        public Reference RemoteId { get; private set; }

        /// <summary>
        /// Local address on remote side
        /// </summary>
        public SocketAddress LocalAddress { get; private set; }

        /// <summary>
        /// Peer address on remote side
        /// </summary>
        public SocketAddress PeerAddress { get; private set; }

        /// <summary>
        /// Bound proxy for this stream
        /// </summary>
        public INameRecord Proxy { get; private set; }

        /// <summary>
        /// Proxy the socket is bound on.  
        /// </summary>
        public SocketAddress ProxyAddress => Proxy.Address.ToSocketAddress();

        /// <summary>
        /// Target buffer block
        /// </summary>
        public ITargetBlock<Message> SendBlock { get; private set; }

        /// <summary>
        /// Source buffer block
        /// </summary>
        public ISourceBlock<Message> ReceiveBlock { get; private set; }

        /// <summary>
        /// Constructor for proxy link object
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="proxy"></param>
        /// <param name="remoteId"></param>
        /// <param name="localAddress"></param>
        /// <param name="peerAddress"></param>
        internal ProxyLink(ProxySocket socket, INameRecord proxy, Reference remoteId, 
            SocketAddress localAddress, SocketAddress peerAddress) {

            _socket = socket ?? throw new ArgumentNullException(nameof(socket));

            Proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
            RemoteId = remoteId ?? throw new ArgumentNullException(nameof(remoteId));
            LocalAddress = localAddress ?? throw new ArgumentNullException(nameof(localAddress));
            PeerAddress = peerAddress ?? throw new ArgumentNullException(nameof(peerAddress));

            SendBlock = _send = new BufferBlock<Message>(new DataflowBlockOptions {
                NameFormat = "Send (in Link) Id={1}",
                EnsureOrdered = true,
                BoundedCapacity = DataflowBlockOptions.Unbounded
            });

            ReceiveBlock = _receive = new BufferBlock<Message>(new DataflowBlockOptions {
                NameFormat = "Receive (in Link) Id={1}",
                EnsureOrdered = true,
                BoundedCapacity = 1
            });
        }

        /// <summary>
        /// Begin open of stream, this provides the connection string for the
        /// remote side, that is passed as part of the open request.
        /// </summary>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async Task<OpenRequest> BeginOpenAsync(CancellationToken ct) {
            try {
                _connection = await _socket.Provider.StreamService.CreateConnectionAsync(
                    _streamId, RemoteId, Proxy).ConfigureAwait(false);

                return new OpenRequest {
                    ConnectionString = _connection.ConnectionString != null ?
                        _connection.ConnectionString.ToString() : "",
                    IsPolled = _connection.IsPolled,
#if !NO_MSG_OVER_8K // TODO: Remove this when > 88 k messages are supported
                    // MaxReceiveBuffer = 800,  
#endif
                    StreamId = _streamId
                };
            }
            catch (OperationCanceledException) {
                return null;
            }
        }

        /// <summary>
        /// Complete connection by waiting for remote side to connect.
        /// </summary>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async Task<bool> TryCompleteOpenAsync(CancellationToken ct) {
            if (_connection == null)
                return false;
            try {
                var stream = await _connection.OpenAsync(ct).ConfigureAwait(false);

                _receiveLink = stream.ReceiveBlock.ConnectTo(_receive);
                _sendLink = _send.ConnectTo(stream.SendBlock);

                return true;
            }
            catch (OperationCanceledException) {
                return false;
            }
        }

        /// <summary>
        /// Send socket option message
        /// </summary>
        /// <param name="option"></param>
        /// <param name="value"></param>
        /// <param name="ct"></param>
        public async Task SetSocketOptionAsync(
            SocketOption option, ulong value, CancellationToken ct) {
            var response = await _socket.Provider.ControlChannel.CallAsync(Proxy,
                    new Message(_socket.Id, RemoteId, new SetOptRequest(
                        new Property<ulong>((uint)option, value))), 
                TimeSpan.MaxValue, ct).ConfigureAwait(false);
            ProxySocket.ThrowIfFailed(response);
        }

        /// <summary>
        /// Get socket option
        /// </summary>
        /// <param name="option"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async Task<ulong> GetSocketOptionAsync(
            SocketOption option, CancellationToken ct) {
            var response = await _socket.Provider.ControlChannel.CallAsync(Proxy,
                    new Message(_socket.Id, RemoteId, new GetOptRequest(
                        option)), 
                TimeSpan.MaxValue, ct).ConfigureAwait(false);
            ProxySocket.ThrowIfFailed(response);
            var optionValue = ((GetOptResponse)response.Content).OptionValue as Property<ulong>;
            if (optionValue == null) {
                throw new ProxyException("Bad option value returned");
            }
            return optionValue.Value;
        }

        /// <summary>
        /// Close link
        /// </summary>
        /// <param name="ct"></param>
        public async Task CloseAsync(CancellationToken ct) {
            var tasks = new Task[] { UnlinkAsync(ct), TerminateConnectionAsync(ct) };
            try {
                // Close both ends
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (AggregateException ae) {
                if (ae.InnerExceptions.Count == tasks.Length) {
                    // Only throw if all tasks failed.
                    throw SocketException.Create("Exception during close", ae);
                }
                ProxyEventSource.Log.HandledExceptionAsInformation(this, ae.Flatten());
            }
            catch (Exception e) {
                ProxyEventSource.Log.HandledExceptionAsInformation(this, e);
            }
        }

        /// <summary>
        /// Close the stream part
        /// </summary>
        /// <param name="ct"></param>
        /// <returns></returns>
        private async Task TerminateConnectionAsync(CancellationToken ct) {
            IConnection connection = _connection;
            _connection = null;
            try {
                ProxyEventSource.Log.StreamClosing(this, null);
                await SendBlock.SendAsync(new Message(_socket.Id, RemoteId, 
                    new CloseRequest()), ct).ConfigureAwait(false);

                SendBlock.Complete();
                ReceiveBlock.Complete();

                await SendBlock.Completion.ConfigureAwait(false);
                await ReceiveBlock.Completion.ConfigureAwait(false);
            }
            finally {
                await connection.CloseAsync().ConfigureAwait(false);
                ProxyEventSource.Log.StreamClosed(this, null);
            }
        }

        /// <summary>
        /// Remove link on remote proxy through rpc
        /// </summary>
        /// <param name="ct"></param>
        /// <returns></returns>
        private async Task UnlinkAsync(CancellationToken ct) {
            try {
                var response = await _socket.Provider.ControlChannel.CallAsync(Proxy,
                    new Message(_socket.Id, RemoteId,
                    new CloseRequest()), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                ProxyEventSource.Log.ObjectDestroyed(this);
                if (response != null) {
                    SocketError errorCode = (SocketError)response.Error;
                    if (errorCode != SocketError.Success &&
                        errorCode != SocketError.Timeout &&
                        errorCode != SocketError.Closed) {
                        throw new SocketException(errorCode);
                    }
                }
            }
            catch (Exception e) when (!(e is SocketException)) {
                throw SocketException.Create("Failed to close", e);
            }
        }

        /// <summary>
        /// Returns a string that represents the current object.
        /// </summary>
        /// <returns>A string that represents the current object.</returns>
        public override string ToString() {
            return 
                $"Link {PeerAddress} through {LocalAddress} on {Proxy} "
              + $"with stream {_streamId} (Socket {_socket})";
        }

        private IConnection _connection;
        private readonly BufferBlock<Message> _send;
        private IDisposable _sendLink;
        private readonly BufferBlock<Message> _receive;
        private IDisposable _receiveLink;
        private readonly ProxySocket _socket;
        private readonly Reference _streamId = new Reference();
    }
}