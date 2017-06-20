// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy.Provider {
    using System;
    using Microsoft.AspNetCore.Builder;

    /// <summary>
    /// Helper extension class
    /// </summary>
    public static class WebsocketExtensions {
        /// <summary>
        /// Attach proxy handler to application
        /// </summary>
        /// <param name="app"></param>
        /// <returns></returns>
        public static void ConfigureProxy(this IApplicationBuilder app, Uri endpoint) {
            app.UseWebSockets();
            app.UseMiddleware<WebsocketProvider>(endpoint, null);
        }

        /// <summary>
        /// Attach proxy handler to application
        /// </summary>
        /// <param name="app"></param>
        /// <param name="iothub"></param>
        /// <returns></returns>
        public static void ConfigureProxy(this IApplicationBuilder app, Uri endpoint,
            string iotHub) {
            app.UseWebSockets();
            app.UseMiddleware<WebsocketProvider>(endpoint, iotHub);
        }
    }
}
