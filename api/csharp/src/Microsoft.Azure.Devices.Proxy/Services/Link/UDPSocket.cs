// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy {
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Dataflow;

    internal class UDPSocket : ProxySocket {

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="info"></param>
        /// <param name="provider"></param>
        internal UDPSocket(SocketInfo info, IProvider provider) :
            base(info, provider) {
            if (info.Type != SocketType.Dgram) {
                throw new ArgumentException("Udp only supports datagrams");
            }
        }

        /// <summary>
        /// Bind udp socket
        /// </summary>
        /// <param name="endpoint"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public override async Task BindAsync(SocketAddress endpoint, CancellationToken ct) {
            await base.BindAsync(endpoint, ct);
            bool connected = await LinkAllAsync(_bindList, endpoint, ct);
            if (!connected) {
                throw new SocketException(
                    "Could not link browse socket on proxy", SocketError.NoHost);
            }
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
            await SendBlock.SendAsync(new Message(null, null, null,
                new DataMessage(buffer, endpoint)), ct).ConfigureAwait(false);
            return buffer.Count;
        }

        /// <summary>
        /// Receive a data packet
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async override Task<ProxyAsyncResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken ct) {
            while (true) {
                var message = await ReceiveBlock.ReceiveAsync(ct).ConfigureAwait(false);
                if (message.TypeId == MessageContent.Close) {

                    // TODO
                }
                else if (message.TypeId == MessageContent.Data) {
                    var data = message.Content as DataMessage;
                    int copy = Math.Min(data.Payload.Length, buffer.Count);
                    Buffer.BlockCopy(data.Payload, 0, buffer.Array, buffer.Offset, copy);
                    return new ProxyAsyncResult {
                        Address = data.Source,
                        Count = copy
                    };
                }
            }
        }

        public override Task ConnectAsync(SocketAddress address, CancellationToken ct) {
            throw new NotSupportedException("Cannot call connect on this socket");
        }

        public override Task ListenAsync(int backlog, CancellationToken ct) {
            throw new NotSupportedException("Cannot call listen on this socket");
        }

    }
}
