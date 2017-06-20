// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy.Provider {
    using Proxy;
    using System;
    using Microsoft.AspNetCore.Http;
    using System.Collections.Concurrent;
    using System.Threading.Tasks;

    /// <summary>
    /// asp.net core middleware that provides streamprovider that uses websockets as stream service
    /// implementation.
    /// </summary>
    public class WebsocketProvider : DefaultProvider, IStreamService {

        /// <summary>
        /// Exposes the new asp.net stream service on the default provider
        /// </summary>
        public override IStreamService StreamService {
            get {
                return this;
            }
        }

        internal ConcurrentDictionary<Reference, WebSocketMessageConnection> _connectionMap =
            new ConcurrentDictionary<Reference, WebSocketMessageConnection>();
        
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="next"></param>
        /// <param name="uri"></param>
        /// <param name="iothub"></param>
        public WebsocketProvider(RequestDelegate next, Uri uri, string iothub) :
            base(iothub) {
            _uri = uri;
            _next = next;
            Socket.Provider = this;
        }

        /// <summary>
        /// Handle all websocket requests
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task Invoke(HttpContext context) {
            try {
                if (context.WebSockets.IsWebSocketRequest && context.Request.IsHttps) {
                    // Correlate the accepted socket to an open stream in our map
                    Reference streamId;
                    if (Reference.TryParse(context.Request.PathBase, out streamId)) {
                        if (_connectionMap.TryGetValue(streamId, out WebSocketMessageConnection connection)) {
                            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                            bool connected = await Task.Run(
                                () => connection.TryConnect(webSocket)).ConfigureAwait(false);
                            if (connected) {
                                ProxyEventSource.Log.ConnectionAccepted(context);
                                return; // Connected
                            }
                        }
                        ProxyEventSource.Log.ConnectionRejected(context, null);
                    }
                }
            }
            catch (Exception e) {
                // Some error occurred
                ProxyEventSource.Log.ConnectionRejected(context, e);
            }
            await _next(context);
        }

        /// <summary>
        /// Returns a new connection
        /// </summary>
        /// <param name="streamId">Local reference id of the stream</param>
        /// <param name="remoteId">Remote reference of link</param>
        /// <param name="proxy">The proxy server</param>
        /// <returns></returns>
        public Task<IConnection> CreateConnectionAsync(Reference streamId,
            Reference remoteId, INameRecord proxy) {
            var uri = new UriBuilder(_uri);
            uri.Scheme = "wss";
            uri.Path = streamId.ToString();
            var connection = new WebSocketMessageConnection(
                this, streamId, new ConnectionString(uri.Uri, "proxy", "secret"));
            _connectionMap.AddOrUpdate(streamId, connection, (r, s) => connection);
            return Task.FromResult((IConnection)connection);
        }

        private Uri _uri;
        private RequestDelegate _next;
    }
}
