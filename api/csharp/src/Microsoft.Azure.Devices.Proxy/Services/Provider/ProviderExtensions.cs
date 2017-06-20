// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy {
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Dataflow;

    internal static class ProviderExtensions {

        /// <summary>
        /// Lookup records by name
        /// </summary>
        /// <param name="name"></param>
        /// <param name="type"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public static async Task<IEnumerable<INameRecord>> LookupAsync(this INameService service, 
            string name, NameRecordType type, CancellationToken ct) {
            var result = new List<INameRecord>();
            var target = new ActionBlock<INameRecord>(n => result.Add(n),
                new ExecutionDataflowBlockOptions { CancellationToken = ct });
            var query = service.ByName(target, ct);
            await query.SendAsync(Tuple.Create(name, type)).ConfigureAwait(false);
            query.Complete();
            await target.Completion.ConfigureAwait(false); 
            return result;
        }

        /// <summary>
        /// Lookup records by address
        /// </summary>
        /// <param name="address"></param>
        /// <param name="type"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public static async Task<IEnumerable<INameRecord>> LookupAsync(this INameService service, 
            Reference address, NameRecordType type, CancellationToken ct) {
            var result = new List<INameRecord>();
            var target = new ActionBlock<INameRecord>(n => result.Add(n), 
                new ExecutionDataflowBlockOptions { CancellationToken = ct });
            var query = service.ByAddress(target, ct);
            await query.SendAsync(Tuple.Create(address, type)).ConfigureAwait(false);
            query.Complete();
            await target.Completion.ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Adds or updates a record in the name service
        /// </summary>
        /// <param name="proxy"></param>
        /// <param name="name"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public static Task AddOrUpdateAsync(this INameService service, 
            INameRecord record, CancellationToken ct) =>
            service.Update.SendAsync(Tuple.Create(record, true), ct);

        /// <summary>
        /// Removes a record in the name service
        /// </summary>
        /// <param name="proxy"></param>
        /// <param name="name"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public static Task RemoveAsync(this INameService service,
            INameRecord record, CancellationToken ct) =>
            service.Update.SendAsync(Tuple.Create(record, false), ct);
    }
}
