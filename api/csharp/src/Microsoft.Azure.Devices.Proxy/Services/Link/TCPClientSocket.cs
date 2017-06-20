// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy {
    using System;
    using System.Linq;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks.Dataflow;
    using System.Threading.Tasks;

    /// <summary>
    /// Concrete tcp proxy socket implementation
    /// </summary>
    internal class TCPClientSocket : ProxySocket {

        /// <summary>
        /// Host record socket is connected to
        /// </summary>
        public INameRecord Host { get; private set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="info"></param>
        /// <param name="provider"></param>
        internal TCPClientSocket(SocketInfo info, IProvider provider) :
            base(info, provider) {
            if (info.Type != SocketType.Stream)
                throw new ArgumentException("Tcp only supports streams");
        }


        /// <summary>
        /// Receives ping responses and handles them one by one through a handler 
        /// callback.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="handler"></param>
        /// <param name="last"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async Task PingAsync(SocketAddress address,
            Func<Message, INameRecord, CancellationToken, Task<Disposition>> handler,
            Action<Exception> last, CancellationToken ct) {
            await Provider.ControlChannel.BroadcastAsync(new Message(
                Id, Reference.Null, new PingRequest(address)), handler, last, ct);
        }


        /// <summary>
        /// Connect to a target on first of bound proxies, or use ping based dynamic lookup
        /// </summary>
        /// <param name="address"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public override async Task ConnectAsync(SocketAddress address, CancellationToken ct) {

            if (address.Family == AddressFamily.Bound) {
                // Unwrap proxy and connect address.  If not bound, use local address to bind to.
                if (_bindList == null) {
                    await BindAsync(((BoundSocketAddress)address).LocalAddress, ct);
                }
                address = ((BoundSocketAddress) address).RemoteAddress;
            }

            // Get the named host from the registry if it exists - there should only be one...
            Host = null;
            var hostList = await Provider.NameService.LookupAsync(
                address.ToString(), NameRecordType.Host, ct).ConfigureAwait(false);
            foreach (var host in hostList) {
                Host = host;
                break;
            }

            // If there is no host in the registry, create a fake host record for this address
            if (Host == null) {
                Host = new NameRecord(NameRecordType.Host, address.ToString());
            }
            else if (!Host.Name.Equals(address.ToString(), StringComparison.CurrentCultureIgnoreCase)) {
                // Translate the address to host address
                address = new ProxySocketAddress(Host.Name);
            }

            // Set bind list before connecting if it is not already set during Bind
            bool autoBind = _bindList == null;
            if (autoBind) {
                var bindList = new HashSet<INameRecord>();
                foreach (var proxyRef in Host.References) {
                    var results = await Provider.NameService.LookupAsync(
                        proxyRef, NameRecordType.Proxy, ct).ConfigureAwait(false);
                    bindList.AddRange(results);
                }
                _bindList = bindList.Any() ? bindList : null;
            }

            bool connected = false;
            if (_bindList != null) {
                // Try to connect through each proxy in the bind list
                foreach (var proxy in _bindList) {
                    connected = await ConnectAsync(address, proxy, ct).ConfigureAwait(false);
                    if (connected) {
                        break;
                    }
                }
                // If there was a bind list and we could not connect through it, throw
                if (!connected && !autoBind) {
                    throw new SocketException(SocketError.NoHost);
                }
                _bindList = null;
            }

            if (!connected) {
                await PingAsync(address, async (response, proxy, ct2) => {
                    if (connected) {
                        return Disposition.Done;
                    }
                    if (response.Error == (int)SocketError.Success) {
                        try {
                            connected = await ConnectAsync(address, proxy, ct).ConfigureAwait(false); 
                        }
                        catch (Exception) {
                            return Disposition.Retry;
                        }
                    }
                    return connected ? Disposition.Done : Disposition.Continue; 
                }, (ex) => {
                    if (!connected) {
                        throw new SocketException(
                            "Could not link socket on proxy", ex, SocketError.NoHost);
                    }
                }, ct).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();
            }

            await Provider.NameService.AddOrUpdateAsync(Host, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Connect to address through a proxy
        /// </summary>
        /// <param name="address"></param>
        /// <param name="proxy"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        private async Task<bool> ConnectAsync(SocketAddress address, INameRecord proxy, 
            CancellationToken ct) {

            bool connected = await LinkOneAsync(proxy, address, ct).ConfigureAwait(false);
            if (connected) {
                Host.AddReference(proxy.Address);
            }
            else {
                Host.RemoveReference(proxy.Address);
            }
            return connected;
        }

        /// <summary>
        /// Select a bind list for a host address if one exists
        /// </summary>
        /// <param name="endpoint"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        private async Task BindAsync(CancellationToken ct) {
            var bindList = new HashSet<INameRecord>();
            foreach (var proxyRef in Host.References) {
                var results = await Provider.NameService.LookupAsync(
                    proxyRef, NameRecordType.Proxy, ct).ConfigureAwait(false);
                bindList.AddRange(bindList);
            }
            _bindList = bindList.Any() ? bindList : null;
        }

        public override Task ListenAsync(int backlog, CancellationToken ct) {
            throw new NotSupportedException("Cannot call listen on client socket!");
        }

        /// <summary>
        /// Send buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="endpoint"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async override Task<int> SendAsync(ArraySegment<byte> buffer,
            SocketAddress endpoint, CancellationToken ct) {
            bool sent = await SendBlock.SendAsync(new Message(null, null, null, 
                new DataMessage(buffer, endpoint)), ct).ConfigureAwait(false);
            return buffer.Count;
        }

#if PERF
        private long _transferred;
        private Stopwatch _transferredw = Stopwatch.StartNew();
#endif

        /// <summary>
        /// Buffered receive
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public override Task<ProxyAsyncResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken ct) {

            if (buffer.Count == 0) {
                return Task.FromResult(new ProxyAsyncResult());
            }

            if (_lastData != null) {
                int copied = CopyBuffer(ref buffer);
                if (copied > 0) {
                    if (_lastRead == null || _lastRead.Result.Count != copied) {
                        var result = new ProxyAsyncResult();
                        result.Count = copied;
                        _lastRead = Task.FromResult(result);
                    }
                    return _lastRead;
                }
            }
            _lastRead = ReceiveInternalAsync(buffer, ct);
            return _lastRead;
        }

        /// <summary>
        /// Receive using async state machine
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        private async Task<ProxyAsyncResult> ReceiveInternalAsync(
            ArraySegment<byte> buffer, CancellationToken ct) {
            var result = new ProxyAsyncResult();
            while (true) {
                if (_lastData == null) {
                    var message = await ReceiveBlock.ReceiveAsync(ct).ConfigureAwait(false);
                    if (message.TypeId == MessageContent.Close) {

                        // TODO

                        break;
                    }
                    else if (message.TypeId != MessageContent.Data) {
                        continue;
                    }

                    _lastData = message.Content as DataMessage;
                    _offset = 0;

                    // Break on 0 sized packets
                    if (_lastData == null || _lastData.Payload.Length == 0) {
                        _lastData = null;
                        break;
                    }
#if PERF
                    _transferred += _lastData.Payload.Length;
                    Console.CursorLeft = 0; Console.CursorTop = 0;
                    Console.WriteLine(
                        $"{ _transferred / _transferredw.ElapsedMilliseconds} kB/sec");
#endif
                }
                result.Count = CopyBuffer(ref buffer);
                if (result.Count > 0)
                    break;
            }
            return result;
        }

        /// <summary>
        /// Copies from the last buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        private int CopyBuffer(ref ArraySegment<Byte> buffer) {
            // How much to copy from the last data buffer.
            int toCopy = Math.Min(buffer.Count, _lastData.Payload.Length - _offset);
            Buffer.BlockCopy(_lastData.Payload, _offset,
                buffer.Array, buffer.Offset, toCopy);
            _offset += toCopy;

            if (_offset >= _lastData.Payload.Length) {
                // Last data exhausted, release
                _lastData = null;
                _offset = 0;
            }
            return toCopy;
        }

        private Task<ProxyAsyncResult> _lastRead;
        private DataMessage _lastData;
        private int _offset;
#if PERF
        private long _transferred;
        private Stopwatch _transferredw = Stopwatch.StartNew();
#endif
    }
}