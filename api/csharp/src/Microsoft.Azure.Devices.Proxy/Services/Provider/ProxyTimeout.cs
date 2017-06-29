// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy {
    using System;

    public class ProxyTimeout : TimeoutException {

        public ProxyTimeout() : 
            base("Proxy operation timed out") {
        }

        public ProxyTimeout(Exception innerException) :
            base("Proxy operation timed out", innerException) {
        }

        public ProxyTimeout(AggregateException innerException) :
            base("Proxy operation timed out", innerException.Flatten()) {
        }
    }
}