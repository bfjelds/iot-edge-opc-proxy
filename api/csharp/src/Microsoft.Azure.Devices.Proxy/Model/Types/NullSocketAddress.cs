﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

// Keep in sync with native layer, in particular order of members!

namespace Microsoft.Azure.Devices.Proxy {
    using System;
    using System.Runtime.Serialization;

    /// <summary>
    /// A null socket address (unspecified)
    /// </summary>
    [DataContract]
    public class NullSocketAddress : SocketAddress, IEquatable<NullSocketAddress> {

        [DataMember(Name = "family", Order = 1)]
        public override AddressFamily Family => AddressFamily.Unspecified;

        /// <summary>
        /// Comparison
        /// </summary>
        /// <param name="that"></param>
        /// <returns></returns>
        public bool Equals(NullSocketAddress that) => that != null;

        public override bool IsEqual(object that) => Equals(that as NullSocketAddress);

        protected override void SetHashCode() {
            MixToHash(AddressFamily.Unspecified);
        }

        /// <summary>
        /// Stringify address
        /// </summary>
        /// <returns></returns>
        public override string ToString() {
            return "null";
        }

        public override ProxySocketAddress AsProxySocketAddress() =>
            new ProxySocketAddress();
    }
}