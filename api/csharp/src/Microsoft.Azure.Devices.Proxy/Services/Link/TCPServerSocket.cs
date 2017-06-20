// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy {
    using System;
    using System.Linq;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Concrete tcp proxy socket implementation
    /// </summary>
    internal class TCPServerSocket : ProxySocket {

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="info"></param>
        /// <param name="provider"></param>
        internal TCPServerSocket(SocketInfo info, IProvider provider) :
            base(info, provider) {
            if (info.Type != SocketType.Stream)
                throw new ArgumentException("Tcp only supports streams");
        }

        /// <summary>
        /// Start listening
        /// </summary>
        /// <param name="backlog"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public override async Task ListenAsync(int backlog, CancellationToken ct) {
            bool listening = false;

            if (_bindList != null) {
                listening = await LinkAllAsync(_bindList, null, ct).ConfigureAwait(false);
            }
            else {
                // Not bound, must be bound
                throw new SocketException(SocketError.NotSupported);
            }
            // Check to see if listen completed
            if (!listening) {
                throw new SocketException(SocketError.NoHost);
            }

            // TODO:
            throw new NotImplementedException();
        }

        public override Task ConnectAsync(SocketAddress address,
            CancellationToken ct) {
            throw new NotSupportedException("Cannot call connect on server socket");
        }

        public override Task<int> SendAsync(ArraySegment<byte> buffer, SocketAddress endpoint, 
            CancellationToken ct) {
            throw new NotSupportedException("Cannot call send on server socket");
        }

        public override Task<ProxyAsyncResult> ReceiveAsync(ArraySegment<byte> buffer, 
            CancellationToken ct) {
            throw new NotSupportedException("Cannot call receive on server socket");
        }
    }
}