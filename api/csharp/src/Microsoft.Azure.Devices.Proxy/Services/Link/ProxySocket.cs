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
    /// Proxy socket implementation, core of System proxy socket and browse socket. 
    /// 
    /// Maintains a list of 1 (tcp) to n (udp, browse) proxy links that it manages,
    /// including  keep alive and re-connects. In addition, it provides input and 
    /// output transform from binary buffer to actual messages that are serialized/
    /// deserialized at the provider level (next level).
    /// </summary>
    public abstract class ProxySocket : IProxySocket, IMessageStream {

        /// <summary>
        /// Reference id for this socket
        /// </summary>
        public Reference Id { get; } = new Reference();

        /// <summary>
        /// Proxy provider implementation to use for communication and lookup.
        /// </summary>
        public IProvider Provider { get; private set; }

        /// <summary>
        /// Information for this socket, exchanged with proxy server.
        /// </summary>
        public SocketInfo Info { get; private set; }

        /// <summary>
        /// Send block - broadcasting to all connected links
        /// </summary>
        public ITargetBlock<Message> SendBlock { get; private set; }

        /// <summary>
        /// Receive block - connected to receive from all links
        /// </summary>
        public ISourceBlock<Message> ReceiveBlock { get; private set; }


        /// <summary>
        /// Constructor - hidden, use Create to create a proxy socket object.
        /// </summary>
        /// <param name="info">Properties that the socket should have</param>
        /// <param name="provider">The provider to use for communication, etc.</param>
        protected ProxySocket(SocketInfo info, IProvider provider) {
            Provider = provider;
            Info = info;

            SendBlock = _send = new BroadcastBlock<Message>((message) => {
                return new Message(message);
            },
            new DataflowBlockOptions {
                NameFormat = "Send (in Socket) Id={1}",
                EnsureOrdered = true,
                BoundedCapacity = 1
            });

            ReceiveBlock = _receive = new TransformManyBlock<Message, Message>((message) => {
                if (message.Error == (int)SocketError.Closed ||
                    message.TypeId == MessageContent.Close) {
                    // Remote side closed
                    OnRemoteClose();
                }
                else if (message.Error != (int)SocketError.Success) {
                    if (!OnReceiveError(message)) {
                        ThrowIfFailed(message);
                    }
                }
                else if (message.TypeId == MessageContent.Data) {
                    return message.AsEnumerable();
                }
                else {
                    // Todo: log error?
                }
                return Enumerable.Empty<Message>();
            },
            new ExecutionDataflowBlockOptions {
                NameFormat = "Receive (in Socket) Id={1}",
                EnsureOrdered = true,
                BoundedCapacity = 1
            });
        }

        /// <summary>
        /// Called when error message is received over stream
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        protected virtual bool OnReceiveError(Message message) => 
            false;

        /// <summary>
        /// Called when we determine that remote side closed
        /// </summary>
        protected virtual void OnRemoteClose() => 
            throw new SocketException("Remote side closed", null, SocketError.Closed);

        /// <summary>
        /// Create real proxy socket based on passed socket description. Creates
        /// a specialized socket based on the protocol, e.g. tcp with sequential
        /// stream or udp with packetized stream.
        /// </summary>
        /// <param name="info"></param>
        /// <param name="provider"></param>
        /// <returns></returns>
        public static ProxySocket Create(SocketInfo info, IProvider provider) {
            /**/ if (info.Protocol == ProtocolType.Tcp) {
                if (0 != (info.Flags & (uint)SocketFlags.Passive)) {
                    return new TCPServerSocket(info, provider);
                }
                else {
                    return new TCPClientSocket(info, provider);
                }
            }
            else if (info.Protocol == ProtocolType.Udp) {
                if (0 != (info.Flags & (uint)SocketFlags.Passive)) {
                    return new UDPSocket(info, provider);
                }
                else {
                    throw new ArgumentException("UDP sockets must be passive.");
                }
            }
            else {
                throw new NotSupportedException("Only UDP and TCP supported right now.");
            }
        }

        /// <summary>
        /// Creates a linker block that for every name record tries to create and open a 
        /// link which is posted to the output.
        /// </summary>
        /// <param name="parallel">Whether to link one at a time (single) or in parallel (all)</param>
        /// <param name="ct">Cancels the link step</param>
        /// <returns></returns>
        protected IPropagatorBlock<DataflowMessage<INameRecord>, IProxyLink> CreateLinkBlock(
            ITargetBlock<DataflowMessage<INameRecord>> error, ExecutionDataflowBlockOptions options) {

            if (options == null) {
                throw new ArgumentNullException(nameof(options));
            }
            var ct = options.CancellationToken;
            if (ct == null) {
                throw new ArgumentNullException(nameof(ct));
            }

            return new TransformManyBlock<DataflowMessage<INameRecord>, IProxyLink>(
            async (input) => {
                var proxy = input.Arg;
                ProxyEventSource.Log.LinkCreate(this, proxy.Name, Info.Address);
                try {
                    // Create link, i.e. perform bind, connect, listen, etc. on proxy
                    Message response = await Provider.ControlChannel.CallAsync(proxy,
                        new Message(Id, Reference.Null, new LinkRequest {
                            Properties = Info
                        }), TimeSpan.MaxValue, ct);

                    var linkResponse = response?.Content as LinkResponse;
                    if (linkResponse == null || response.Error != (int)SocketError.Success) {
                        error.Push(input, new ProxyException(
                            response == null ? SocketError.NoHost : (SocketError)response.Error));
                        return Enumerable.Empty<IProxyLink>();
                    }

                    // now create local link and open link for streaming
                    var link = new ProxyLink(this, proxy, linkResponse.LinkId,
                        linkResponse.LocalAddress, linkResponse.PeerAddress);
                    try {
                        // Broker connection string to proxy
                        var openRequest = await link.BeginOpenAsync(ct).ConfigureAwait(false);
                        ProxyEventSource.Log.LinkOpen(this, proxy.Name, Info.Address);

                        await Provider.ControlChannel.CallAsync(proxy, new Message(Id, linkResponse.LinkId,
                            openRequest), TimeSpan.MaxValue, ct).ConfigureAwait(false);

                        // Wait until remote side opens stream connection
                        bool success = await link.TryCompleteOpenAsync(ct).ConfigureAwait(false);
                        if (success) {

                            // Link Send and receive blocks to the socket
                            _send.ConnectTo(link.SendBlock);
                            link.ReceiveBlock.ConnectTo(_receive);

                            ProxyEventSource.Log.LinkComplete(this, proxy.Name, Info.Address);
                            return link.AsEnumerable();
                        }
                    }
                    catch (Exception e) {
                        // Try to close remote side
                        await link.CloseAsync(new CancellationTokenSource(
                            TimeSpan.FromSeconds(10)).Token).ConfigureAwait(false);
                        throw e;
                    }
                }
                catch (ProxyNotFound pnf) {
                    // The proxy was not reachable - try again since it must know the address.
                    error.Push(input, pnf);
                }
                catch (ProxyTimeout pte) {
                    // The proxy request timed out - try again with increased timeout
                    error.Push(input, pte);
                }
                catch (OperationCanceledException) {
                }
                catch (Exception e) {
                    // Some other exception occurred, log as error and give up...
                    ProxyEventSource.Log.HandledExceptionAsWarning(this, e);
                }
                return Enumerable.Empty<IProxyLink>();
            },
            options);
        }

        /// <summary>
        /// Creates a pinger block that sends a ping and returns 
        /// </summary>
        /// <param name="address">Address to ping for</param>
        /// <param name="timeout">Timeout of ping request</param>
        /// <param name="ct">Cancels the ping step(s)</param>
        /// <returns></returns>
        protected IPropagatorBlock<DataflowMessage<INameRecord>, DataflowMessage<INameRecord>> CreatePingBlock(
            ITargetBlock<DataflowMessage<INameRecord>> error, SocketAddress address, ExecutionDataflowBlockOptions options) {

            var timeoutInSeconds = 5;  // Initial timeout is 5 seconds, increases with each error...
            if (options == null) {
                throw new ArgumentNullException(nameof(options));
            }
            var ct = options.CancellationToken;
            if (ct == null) {
                throw new ArgumentNullException(nameof(ct));
            }

            return new TransformManyBlock<DataflowMessage<INameRecord>, DataflowMessage<INameRecord>>(
            async (input) => {
                var record = input.Arg;
                Message response = null;
                try {
                    // Increase timeout up to max timeout based on number of exceptions
                    var pingTimeout = TimeSpan.FromSeconds(
                        timeoutInSeconds * (input.Exceptions.Count + 1));

                    // Do the call
                    response = await Provider.ControlChannel.CallAsync(record,
                        new Message(Id, Reference.Null, new PingRequest(address)),
                            pingTimeout, ct).ConfigureAwait(false);

                    var result = response?.Content as PingResponse;
                    if (result != null) {
                        if (response.Error == (int)SocketError.Success) {
                            return input.AsEnumerable();
                        }
                    }
                }
                catch (ProxyNotFound) {
                    // Proxy not found - that is ok during ping as we only care 
                    // about active ones that can make the connection
                }
                catch (ProxyTimeout pte) {
                    // The proxy request timed out - requeue to increase timeout
                    // Possibly another proxy will respond

                    error.Push(input, pte);
                }
                catch (OperationCanceledException) {
                }
                catch (Exception e) {
                    // Some other exception occurred, log as error and give up...
                    ProxyEventSource.Log.HandledExceptionAsWarning(this, e);
                }
                ProxyEventSource.Log.PingFailure(this, record, address, response);
                return Enumerable.Empty<DataflowMessage<INameRecord>>();
            }, 
            options);
        }

        /// <summary>
        /// Proxy address
        /// </summary>
        public abstract SocketAddress ProxyAddress { get; }

        /// <summary>
        /// Local address
        /// </summary>
        public abstract SocketAddress LocalAddress { get; }

        /// <summary>
        /// Peer address connected to if any.
        /// </summary>
        public abstract SocketAddress PeerAddress { get; }

        /// <summary>
        /// Select the proxy to bind to
        /// </summary>
        /// <param name="address"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public abstract Task BindAsync(SocketAddress address, CancellationToken ct);

        /// <summary>
        /// Connect - only for tcp
        /// </summary>
        /// <param name="address"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public abstract Task ConnectAsync(SocketAddress address, CancellationToken ct);

        /// <summary>
        /// Listen - only for tcp
        /// </summary>
        /// <param name="backlog"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public abstract Task ListenAsync(int backlog, CancellationToken ct);

        /// <summary>
        /// Send buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="endpoint"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public abstract Task<int> SendAsync(ArraySegment<byte> buffer,
            SocketAddress endpoint, CancellationToken ct);

        /// <summary>
        /// Receive buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public abstract Task<ProxyAsyncResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken ct);

        /// <summary>
        /// Close all socket streams and thus this socket
        /// </summary>
        /// <param name="ct"></param>
        public abstract Task CloseAsync(CancellationToken ct);

        /// <summary>
        /// Send socket option message to all streams
        /// </summary>
        /// <param name="option"></param>
        /// <param name="value"></param>
        /// <param name="ct"></param>
        public abstract Task SetSocketOptionAsync(SocketOption option, ulong value,
            CancellationToken ct);

        /// <summary>
        /// Get socket option
        /// </summary>
        /// <param name="option"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public abstract Task<ulong> GetSocketOptionAsync(SocketOption option,
            CancellationToken ct);

        public virtual void Dispose() {
            // No-op
        }

        /// <summary>
        /// Returns a string that represents the socket.
        /// </summary>
        /// <returns>A string that represents the socket.</returns>
        public override string ToString() => $"Socket {Id} : {Info}";

        //
        // Helper to throw if error code is not success
        //
        internal static void ThrowIfFailed(Message response) {
            if (response == null) {
                throw new SocketException(SocketError.Fatal);
            }
            SocketError errorCode = (SocketError)response.Error;
            if (errorCode != SocketError.Success &&
                errorCode != SocketError.Timeout) {
                throw new SocketException(errorCode);
            }
        }


        protected readonly Dictionary<SocketOption, ulong> _optionCache = 
            new Dictionary<SocketOption, ulong>();
        protected readonly BroadcastBlock<Message> _send;
        protected readonly TransformManyBlock<Message, Message> _receive;
    }
}
