// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy {
    using System;
    using System.Threading;
    using System.Threading.Tasks.Dataflow;

    /// <summary>
    /// Interface for name services exposed by provider object
    /// </summary>
    public interface INameService {

        /// <summary>
        /// Produces name records for a particular device id and type. The name
        /// can also be a stringified address, or an alias name of the record. It
        /// cannot be null or empty.
        /// </summary>
        /// <param name="results">target block to post results</param>
        /// <param name="ct">Cancel the entire query</param>
        /// <returns>Target block to post query</returns>
        ITargetBlock<Tuple<string, NameRecordType>> ByName(ITargetBlock<INameRecord> results,
            CancellationToken ct);

        /// <summary>
        /// Produces name records for an address and type.  It is valid
        /// to specify Reference.All to receive all records of a particular
        /// type. It is not valid to specify null or Reference.Null as address.
        /// </summary>
        /// <param name="query">target block to post query</param>
        /// <param name="ct">Cancel the entire query</param>
        /// <returns></returns>
        ITargetBlock<Tuple<Reference, NameRecordType>> ByAddress(ITargetBlock<INameRecord> results,
            CancellationToken ct);

        /// <summary>
        /// Post name record to add or update (true) the record in the name service
        /// or remove it (false).
        /// </summary>
        ITargetBlock<Tuple<INameRecord, bool>> Update { get; }
    }
}
