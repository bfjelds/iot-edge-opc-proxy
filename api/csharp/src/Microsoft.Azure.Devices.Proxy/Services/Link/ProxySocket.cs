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
            // Create specializations for tcp and udp
            /**/ if (info.Protocol == ProtocolType.Tcp) {
                if (0 != (info.Flags & (uint)SocketFlags.Passive)) {
                    return new TCPServerSocket(info, provider);
                }
                return new TCPClientSocket(info, provider);
            }
            else if (info.Protocol == ProtocolType.Udp) {
                return new UDPSocket(info, provider);
            }
            else {
                throw new NotSupportedException("Only UDP and TCP supported right now.");
            }
        }








        /// <summary>
        /// Perform a link handshake with the passed proxies and populate streams
        /// </summary>
        /// <param name="proxies">The proxies (interfaces) to bind the link on</param>
        /// <param name="address">Address to connect to, or null if passive</param>
        /// <param name="ct">Cancels operation</param>
        /// <returns></returns>
        public async Task<bool> LinkAllAsync(IEnumerable<INameRecord> proxies, SocketAddress address, 
            CancellationToken ct) {

            // Complete socket info
            Info.Address = address;

            if (Info.Address == null) {
                Info.Address = new NullSocketAddress();
                Info.Flags |= (uint)SocketFlags.Passive;
            }
            Info.Options.UnionWith(_optionCache.Select(p => new Property<ulong>(
                (uint)p.Key, p.Value)));

            var tasks = new List<Task<IProxyLink>>();
            foreach (var proxy in proxies) {
                if (proxy == null)
                    break;
                tasks.Add(CreateLinkAsync(proxy, ct));
            }
            try {
                var results = await Task.WhenAll(tasks.ToArray()).ConfigureAwait(false);
                Links.AddRange(results.Where(v => v != null));

                return results.Any();
            }
            catch (Exception ex) {
                ProxyEventSource.Log.HandledExceptionAsInformation(this, ex);
                // continue...
            }
            return false;
        }

        /// <summary>
        /// Perform excatly one or zero link handshakes with one of the passed proxies 
        /// and populate streams
        /// </summary>
        /// <param name="proxy">The proxy to bind the link on</param>
        /// <param name="address">Address to connect to, or null if proxy bound</param>
        /// <param name="ct">Cancels operation</param>
        /// <returns></returns>
        public async Task<bool> LinkOneAsync(INameRecord proxy, SocketAddress address, 
            CancellationToken ct) {

            // Complete socket info
            Info.Address = address ?? new NullSocketAddress();
            Info.Flags = address != null ? 0 : (uint)SocketFlags.Passive;
            Info.Options.UnionWith(_optionCache.Select(p => new Property<ulong>(
                (uint)p.Key, p.Value)));

            try {
                var link = await CreateLinkAsync(proxy, ct).ConfigureAwait(false);
                if (link != null) {
                    Links.Add(link);
                    return true;
                }
            }
            catch(Exception ex) {
                ProxyEventSource.Log.HandledExceptionAsInformation(this, ex);
                // continue...
            }
            return false;
        }

        /// <summary>
        /// Create and open link to proxy
        /// </summary>
        /// <param name="proxy"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        private async Task<IProxyLink> CreateLinkAsync(INameRecord proxy, 
            CancellationToken ct) {

            ProxyEventSource.Log.LinkCreate(this, proxy.Name, Info.Address);
            // Create link, i.e. perform bind, connect, listen, etc. on proxy
            Message response = await Provider.ControlChannel.CallAsync(proxy,
                new Message(Id, Reference.Null, new LinkRequest {
                    Properties = Info
                }), ct);
            if (response == null || response.Error != (int)SocketError.Success) {
                ProxyEventSource.Log.LinkFailure(this, proxy.Name, Info, response, null);
                return null;
            }

            var linkResponse = response.Content as LinkResponse;
            if (linkResponse == null) {
                ProxyEventSource.Log.LinkFailure(this, proxy.Name, Info, response, null);
                return null;
            }

            // now create local link and open link for streaming
            var link = new ProxyLink(this, proxy, linkResponse.LinkId,
                linkResponse.LocalAddress, linkResponse.PeerAddress);
            try {
                // Broker connection string to proxy
                var openRequest = await link.BeginOpenAsync(ct).ConfigureAwait(false);
                ProxyEventSource.Log.LinkOpen(this, proxy.Name, Info.Address);

                await Provider.ControlChannel.CallAsync(proxy,
                    new Message(Id, linkResponse.LinkId, openRequest), ct).ConfigureAwait(false);

                // Wait until remote side opens stream connection
                bool success = await link.TryCompleteOpenAsync(ct).ConfigureAwait(false);
                if (success) {

                    // Link Send and receive blocks to the socket
                    SendBlock.LinkTo(link.SendBlock);
                    link.ReceiveBlock.LinkTo(ReceiveBlock);

                    ProxyEventSource.Log.LinkComplete(this, proxy.Name, Info.Address);
                    return link;
                }
            }
            catch (Exception e) {
                // Try to close remote side
                await link.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                ProxyEventSource.Log.LinkFailure(this, proxy.Name, Info.Address, null, e);
            }
            return null;
        }

        /// <summary>
        /// Close and release link
        /// </summary>
        /// <param name="link"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        private async Task ReleaseLinkAsync(IProxyLink link, CancellationToken ct) {
            if (Links.Remove(link)) {
                await link.CloseAsync(ct);
            }
        }




        //
        // LookupBlock(SQL) => batches or records
        // 
        // InvokeMethodTransformBlock -> record + message
        //
        // BufferBlock - Receive response from 
        //
        // ReceiveAsync from BufferBlock - Once done cancel all
        //

        




        /// <summary>
        /// Select the proxy to bind to
        /// </summary>
        /// <param name="endpoint"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public virtual async Task BindAsync(SocketAddress endpoint, CancellationToken ct) {
            // Proxy selected, look up name records for the proxy address

            if (endpoint.Family == AddressFamily.Bound) {
                // Unwrap bound address
                endpoint = ((BoundSocketAddress)endpoint).LocalAddress;
            }

            IEnumerable<SocketAddress> addresses;
            if (endpoint.Family == AddressFamily.Collection) {
                // Unwrap collection
                addresses = ((SocketAddressCollection) endpoint).Addresses();
            }
            else {
                addresses = endpoint.AsEnumerable();
            }

            var bindList = new HashSet<INameRecord>();
            foreach (var address in addresses) {
                var result = await Provider.NameService.LookupAsync(
                    address.ToString(), NameRecordType.Proxy, ct).ConfigureAwait(false);
                bindList.AddRange(result);
            }
            if (!bindList.Any()) {
                throw new SocketException(SocketError.NoAddress);
            }
            _bindList = bindList;
        }

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
        /// Close all socket streams and thus this socket
        /// </summary>
        /// <param name="ct"></param>
        public virtual async Task CloseAsync(CancellationToken ct) {
            try {
                var links = Links.ToArray();
                await Task.WhenAll(links.Select(i => ReleaseLinkAsync(i, 
                    ct))).ConfigureAwait(false);
            }
            catch (Exception e) {
                throw new SocketException(e);
            }
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

        protected IEnumerable<INameRecord> _bindList;
        private readonly Dictionary<SocketOption, ulong> _optionCache = 
            new Dictionary<SocketOption, ulong>();
    }
}
