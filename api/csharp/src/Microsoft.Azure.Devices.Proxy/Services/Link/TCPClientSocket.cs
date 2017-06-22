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

        public override Task ListenAsync(int backlog, CancellationToken ct) {
            throw new NotSupportedException("Cannot call listen on client socket!");
        }

        /// <summary>
        /// Select the proxy to bind to
        /// </summary>
        /// <param name="endpoint"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public override Task BindAsync(SocketAddress endpoint, CancellationToken ct) {
            if (_boundEndpoint != null) {
                throw new SocketException(
                    "Cannot double bind already bound socket. Use collection address.");
            }
            _boundEndpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

            while (_boundEndpoint.Family == AddressFamily.Bound) {
                // Unwrap bound address
                _boundEndpoint = ((BoundSocketAddress)_boundEndpoint).LocalAddress;
            }

            return TaskEx.Completed;
        }

        /// <summary>
        /// Connect to a target on first of bound proxies, or use ping based dynamic lookup
        /// </summary>
        /// <param name="address"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public override async Task ConnectAsync(SocketAddress address, CancellationToken ct) {
            //
            // The address is a combination of proxy binding and remote address.  This is 
            // the case for all addresses returned by Dns resolution.  If no address was 
            // previously bound - use the one provided here.
            //
            Info.Address = address;
            if (Info.Address.Family == AddressFamily.Bound) {
                // Unwrap proxy and connect address.  If not bound, use local address to bind to.
                if (_boundEndpoint == null) {
                    _boundEndpoint = address;
                    // Unwrap bound address
                    while (_boundEndpoint.Family == AddressFamily.Bound) {
                        _boundEndpoint = ((BoundSocketAddress)_boundEndpoint).LocalAddress;
                    }
                }
                Info.Address = ((BoundSocketAddress)Info.Address).RemoteAddress;
            }

            //
            // Get the named host from the registry if it exists - there should only be one...
            // This is the host we shall connect to.  It can contain proxies to use as well.
            //
            var hostList = await Provider.NameService.LookupAsync(
                Info.Address.ToString(), NameRecordType.Host, ct).ConfigureAwait(false);
            Host = hostList.FirstOrDefault();
            if (Host == null) {
                // If there is no host in the registry, create a fake host record for this address
                Host = new NameRecord(NameRecordType.Host, Info.Address.ToString());
            }
            else if (!Host.Name.Equals(Info.Address.ToString(), StringComparison.CurrentCultureIgnoreCase)) {
                // Translate the address to host address
                Info.Address = new ProxySocketAddress(Host.Name);
            }

            // Commit all options that were set until now into info
            Info.Options.UnionWith(_optionCache.Select(p => new Property<ulong>(
                (uint)p.Key, p.Value)));

            //
            // Create tpl network for connect - prioritize input above errored attempts using
            // prioritized scheduling queue.
            //
            var input = DataflowMessage<INameRecord>.CreateAdapter(new ExecutionDataflowBlockOptions {
                CancellationToken = ct,
                MaxDegreeOfParallelism = DataflowBlockOptions.Unbounded,
                MaxMessagesPerTask = DataflowBlockOptions.Unbounded,
                TaskScheduler = QueuedTaskScheduler.Priority[127]
            });

            var errors = new TransformBlock<DataflowMessage<INameRecord>, DataflowMessage<INameRecord>>(
            async (error) => {
                Console.WriteLine($"Error connecting to {Host}");
                Host.RemoveReference(error.Arg.Address);
                await Provider.NameService.Update.SendAsync(Tuple.Create(Host, true), ct);
                return error;
            },
            new ExecutionDataflowBlockOptions {
                CancellationToken = ct,
                MaxDegreeOfParallelism = DataflowBlockOptions.Unbounded,
                MaxMessagesPerTask = DataflowBlockOptions.Unbounded,
                TaskScheduler = QueuedTaskScheduler.Priority[255]
            });

            var query = Provider.NameService.Lookup(new ExecutionDataflowBlockOptions {
                CancellationToken = ct,
                MaxDegreeOfParallelism = DataflowBlockOptions.Unbounded,
                MaxMessagesPerTask = DataflowBlockOptions.Unbounded,
                TaskScheduler = QueuedTaskScheduler.Priority[127]
            });

            var pinger = CreatePingBlock(errors, Info.Address, new ExecutionDataflowBlockOptions {
                CancellationToken = ct,
                MaxDegreeOfParallelism = DataflowBlockOptions.Unbounded,
                MaxMessagesPerTask = 1,
                TaskScheduler = QueuedTaskScheduler.Priority[127]
            });

            var linker = CreateLinkBlock(errors, new ExecutionDataflowBlockOptions {
                CancellationToken = ct,
                MaxDegreeOfParallelism = 1, // Ensure one link is created at a time.
                MaxMessagesPerTask = DataflowBlockOptions.Unbounded,
                TaskScheduler = QueuedTaskScheduler.Priority[127]
            });

            var connection = new WriteOnceBlock<IProxyLink>(l => l, new DataflowBlockOptions {
                MaxMessagesPerTask = 1, // Auto complete when loink is created
                TaskScheduler = QueuedTaskScheduler.Priority[127]
            });

            query.ConnectTo(input);
            input.ConnectTo(pinger);
            errors.ConnectTo(pinger);
            pinger.ConnectTo(linker);
            linker.ConnectTo(connection);

            // Now post proxies to the lookup block - start by sending all bound addresses
            var queries = new List<Task<bool>>();
            if (_boundEndpoint != null) {
                // Todo - consider removing ping and try link only here...

                if (_boundEndpoint.Family == AddressFamily.Collection) {
                    foreach (var item in ((SocketAddressCollection)_boundEndpoint).Addresses()) {
                        queries.Add(query.SendAsync(Provider.NameService.NewQuery(
                            item.ToString(), NameRecordType.Proxy), ct));
                    }
                }
                else {
                    queries.Add(query.SendAsync(Provider.NameService.NewQuery(
                        _boundEndpoint.ToString(), NameRecordType.Proxy), ct));
                }
            }
            else {
                // Auto bind - send references for the host first
                foreach (var item in Host.References) {
                    queries.Add(query.SendAsync(Provider.NameService.NewQuery(
                        item, NameRecordType.Proxy), ct));
                }

                // Post remainder...
                queries.Add(query.SendAsync(Provider.NameService.NewQuery(
                    Reference.All, NameRecordType.Proxy), ct));
            }

            await Task.WhenAll(queries.ToArray());
            // Finalize query operations.
            query.Complete();

            // Wait until a connected link is received.  Then cancel the remainder of the pipeline.
            Links.Add(await connection.ReceiveAsync(ct));
            connection.Complete();
            Provider.NameService.Update.Post(Tuple.Create(Host, true));
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
        private SocketAddress _boundEndpoint;
        private DataMessage _lastData;
        private int _offset;
#if PERF
        private long _transferred;
        private Stopwatch _transferredw = Stopwatch.StartNew();
#endif
    }
}