// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy {
    using System;

    public class ProxyTimeout : TimeoutException {

        public ProxyTimeout() : 
            this(null) {
        }

        public ProxyTimeout(Exception innerException) :
            base("Proxy operation timed out", innerException) {
        }
    }

}