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
    public abstract class ProxySocket : IProxySocket, IMessageStream, IDisposable {

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
        /// List of proxy links - i.e. open sockets or bound sockets on the remote
        /// proxy server.  This is a list of links allowing this socket to create 
        /// aggregate and broadcast type networks across multiple proxies.
        /// </summary>
        protected List<IProxyLink> Links { get; } = new List<IProxyLink>();

        /// <summary>
        /// Returns an address representing the proxy address(s)
        /// </summary>
        public SocketAddress ProxyAddress {
            get {
                return SocketAddressCollection.Create(
                    Links.Where(l => l.ProxyAddress != null).Select(l => l.ProxyAddress));
            }
        }

        /// <summary>
        /// Returns an address representing the address(es) bound on proxy
        /// </summary>
        public SocketAddress LocalAddress {
            get {
                return SocketAddressCollection.Create(
                    Links.Where(l => l.LocalAddress != null).Select(l => l.LocalAddress));
            }
        }

        /// <summary>
        /// Returns an address representing the peer(s) of all links.
        /// </summary>
        public SocketAddress PeerAddress {
            get {
                return SocketAddressCollection.Create(
                    Links.Where(l => l.PeerAddress != null).Select(l => l.PeerAddress));
            }
        }

        /// <summary>
        /// Send block - broadcasting to all connected links
        /// </summary>
        public IPropagatorBlock<Message, Message> SendBlock { get; } =
            new BroadcastBlock<Message>(message => new Message(message));


        /// <summary>
        /// Receive block - connected to receive from all links
        /// </summary>
        public IPropagatorBlock<Message, Message> ReceiveBlock { get; } =
            new TransformBlock<Message, Message>((message) => {
                if (message.TypeId == MessageContent.Close) {
                    // Remote side closed, close link
                    return null;
                    // todo:
                }
                else if (message.TypeId != MessageContent.Data) {
                    return null;
                    // todo:
                }
                return message;
            });


        /// <summary>
        /// Constructor - hidden, use Create to create a proxy socket object.
        /// </summary>
        /// <param name="info">Properties that the socket should have</param>
        /// <param name="provider">The provider to use for communication, etc.</param>
        protected ProxySocket(SocketInfo info, IProvider provider) {
            Provider = provider;
            Info = info;
        }

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
        protected IPropagatorBlock<DataflowMessage<INameRecord>, IProxyLink> Linker(
            ITargetBlock<DataflowMessage<INameRecord>> error,
            bool parallel, CancellationToken ct) {

            var output = new BufferBlock<IProxyLink>();
            var linker = new ActionBlock<DataflowMessage<INameRecord>>(async (input) => {
                var proxy = input.Arg;
                ProxyEventSource.Log.LinkCreate(this, proxy.Name, Info.Address);
                // Create link, i.e. perform bind, connect, listen, etc. on proxy
                Message response = await Provider.ControlChannel.CallAsync(proxy,
                    new Message(Id, Reference.Null, new LinkRequest {
                        Properties = Info
                    }), TimeSpan.MaxValue, ct);

                var linkResponse = response?.Content as LinkResponse;
                if (linkResponse == null || response.Error != (int)SocketError.Success) {
                    ProxyEventSource.Log.LinkFailure(this, proxy.Name, Info, response, null);
                    error.Push(input, new ProxyException((SocketError)response.Error));
                    return;
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
                        SendBlock.LinkTo(link.SendBlock);
                        link.ReceiveBlock.LinkTo(ReceiveBlock);

                        ProxyEventSource.Log.LinkComplete(this, proxy.Name, Info.Address);
                        output.Post(link);
                    }
                }
                catch (Exception e) {
                    // Try to close remote side
                    await link.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                    error.Push(input, e);
                    ProxyEventSource.Log.LinkFailure(this, proxy.Name, Info.Address, null, e);
                }

            }, new ExecutionDataflowBlockOptions {
                MaxDegreeOfParallelism = parallel ? Environment.ProcessorCount : 1,
                CancellationToken = ct
            });
            return DataflowBlock.Encapsulate(linker, output);
        }

        /// <summary>
        /// Creates a pinger block that sends a ping and returns 
        /// </summary>
        /// <param name="address">Address to ping for</param>
        /// <param name="timeout">Timeout of ping request</param>
        /// <param name="ct">Cancels the ping step(s)</param>
        /// <returns></returns>
        protected IPropagatorBlock<DataflowMessage<INameRecord>, DataflowMessage<INameRecord>> Pinger(
            ITargetBlock<DataflowMessage<INameRecord>> error, 
            SocketAddress address, CancellationToken ct) {

            var timeoutInSeconds = 5;  // Initial timeout is 5 seconds, increases with each error...
            var output = new BufferBlock<DataflowMessage<INameRecord>>();

            var pinger = new ActionBlock<DataflowMessage<INameRecord>>(async (input) => {
                var record = input.Arg;
                try {
                    // Increase timeout up to max timeout based on number of exceptions
                    var pingTimeout = TimeSpan.FromSeconds(
                        timeoutInSeconds * (input.Exceptions.Count + 1));

                    // Do the call
                    var response = await Provider.ControlChannel.CallAsync(record,
                        new Message(Id, Reference.Null, new PingRequest(address)),
                            pingTimeout, ct).ConfigureAwait(false);

                    var result = response?.Content as PingResponse;
                    if (result != null) {
                        if (response.Error == (int)SocketError.Success) {
                            output.Push(input);
                        }
                        else {
                            // Log
                        }
                    }
                    else {
                        // Unexpected 
                        // Log as error!
                        // Todo
                    }
                }
                catch (ProxyNotFound pnf) {
                    // The proxy was not reachable - try again later
                    ProxyEventSource.Log.HandledExceptionAsInformation(this, pnf);
                    error.Push(input, null);
                }
                catch (ProxyTimeout pte) {
                    // The proxy request timed out - increase timeout...
                    ProxyEventSource.Log.HandledExceptionAsInformation(this, pte);
                    error.Push(input, pte);
                }
                catch (Exception e) {
                    ProxyEventSource.Log.HandledExceptionAsWarning(this, e);
                    // Log as error and continue...
                }
            }, 
            new ExecutionDataflowBlockOptions {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct
            });
            return DataflowBlock.Encapsulate(pinger, output);
        }

        /// <summary>
        /// Bind to provided endpoint(s) - return on first success
        /// </summary>
        /// <param name="endpoint"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        protected async Task LinkAsync(SocketAddress endpoint, CancellationToken ct) {

            // Complete socket info
            Info.Options.UnionWith(_optionCache.Select(p => new Property<ulong>(
                (uint)p.Key, p.Value)));

            //
            // Create tpl network for connect - prioritize input above errored attempts using
            // prioritized scheduling queue.
            //
            var tcs = new TaskCompletionSource<bool>();
            ct.Register(() => tcs.TrySetCanceled());

            var input = DataflowBlockEx.DataflowMessageTransformBlock<INameRecord>(
                new ExecutionDataflowBlockOptions {
                    CancellationToken = _open.Token,
                    TaskScheduler = _scheduler.ActivateNewQueue(0)
                });
            var errors = new BufferBlock<DataflowMessage<INameRecord>>(
                new DataflowBlockOptions {
                    CancellationToken = _open.Token,
                    TaskScheduler = _scheduler.ActivateNewQueue(1)
                });

            var query = Provider.NameService.Lookup(input, _open.Token);
            var linker = Linker(errors, false, _open.Token);

            // When first connected mark tcs as complete
            var connected = new ActionBlock<IProxyLink>(l => {
                if (!Links.Any()) {
                    tcs.TrySetResult(true);
                }
                Links.Add(l);
            });
            // If no one connected but connected action completes, throw error.
            var completed = connected.Completion.ContinueWith(_ => {
                if (!Links.Any()) {
                    tcs.TrySetException(new ProxyException(SocketError.NoHost));
                }
            });

            input.LinkTo(linker);
            errors.LinkTo(linker);
            linker.LinkTo(connected);

            //
            // Query generates name records from device registry based on the passed proxy information.
            // these are then posted to the linker for linking.  When the first link is established
            // Connect returns successful, but proxy continues linking until disposed
            //
            while (endpoint.Family == AddressFamily.Bound) {
                // Unwrap bound address
                endpoint = ((BoundSocketAddress)endpoint).LocalAddress;
            }
            if (endpoint == null || endpoint is AnySocketAddress) {
                query.Post(Provider.NameService.NewQuery(Reference.All, NameRecordType.Proxy));
            }
            else {
                if (endpoint.Family == AddressFamily.Collection) {
                    foreach (var item in ((SocketAddressCollection)endpoint).Addresses()) {
                        await query.SendAsync(Provider.NameService.NewQuery(
                            item.ToString(), NameRecordType.Proxy), ct);
                    }
                }
                else {
                    query.Post(Provider.NameService.NewQuery(endpoint.ToString(), NameRecordType.Proxy));
                }
            }

            // Finalize query - all queries were sent.
            query.Complete();
            // Signalled when the first one completed or user cancelled
            await tcs.Task.ConfigureAwait(false);
        }


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
        public virtual async Task CloseAsync(CancellationToken ct) {
            try {
                var links = Links.ToArray();
                await Task.WhenAll(links.Select(l => l.CloseAsync(ct))).ConfigureAwait(false);
            }
            catch (Exception e) {
                throw new SocketException(e);
            }
            finally {
                _open.Cancel(true);
            }
        }

        /// <summary>
        /// Send socket option message to all streams
        /// </summary>
        /// <param name="option"></param>
        /// <param name="value"></param>
        /// <param name="ct"></param>
        public async Task SetSocketOptionAsync(SocketOption option, ulong value,
            CancellationToken ct) {
            if (!Links.Any()) {
                _optionCache[option] = value;
            }
            try {
                await Task.WhenAll(Links.Select(
                    i => i.SetSocketOptionAsync(option, value, ct))).ConfigureAwait(false);
            }
            catch (Exception e) {
                throw new SocketException(e);
            }
        }

        /// <summary>
        /// Get socket option
        /// </summary>
        /// <param name="option"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async Task<ulong> GetSocketOptionAsync(SocketOption option,
            CancellationToken ct) {
            if (!Links.Any()) {
                return _optionCache.ContainsKey(option) ? _optionCache[option] : 0;
            }
            var cts = new CancellationTokenSource();
            ct.Register(() => {
                cts.Cancel();
            });
            var tasks = Links.Select(
                i => i.GetSocketOptionAsync(option, cts.Token)).ToList();
            Exception e = null;
            while (tasks.Count > 0) {
                var result = await Task.WhenAny(tasks).ConfigureAwait(false);
                try {
                    ulong value = await result.ConfigureAwait(false);
                    cts.Cancel(); // Cancel the rest
                    return value;
                }
                catch (Exception thrown) {
                    tasks.Remove(result);
                    e = thrown;
                }
            }
            throw new SocketException(e);
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

        public void Dispose() {
            _open.Cancel(false);
        }

        protected CancellationTokenSource _open = new CancellationTokenSource();
        protected readonly Dictionary<SocketOption, ulong> _optionCache = 
            new Dictionary<SocketOption, ulong>();
        protected static readonly QueuedTaskScheduler _scheduler =
            new QueuedTaskScheduler();
    }
}
