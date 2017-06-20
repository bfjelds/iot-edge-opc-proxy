// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy {
    using System;

    public class ProxyPermission : ProxyException {

        public ProxyPermission() :
            this(null) {
        }

        public ProxyPermission(Exception innerException) :
            base("Proxy access not authorized", innerException) {
        }
    }

}