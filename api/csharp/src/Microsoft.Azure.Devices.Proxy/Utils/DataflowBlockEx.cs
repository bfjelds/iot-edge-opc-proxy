// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace System.Threading.Tasks.Dataflow {
    using System;
    public static class DataflowBlockEx {

        public static bool Push<T>(this ITargetBlock<DataflowMessage<T>> block,
            DataflowMessage<T> message, Exception exception) {
            message.Exceptions.AddFirst(exception);
            return block.Post(message);
        }

        public static bool Push<T>(this ITargetBlock<DataflowMessage<T>> block,
            DataflowMessage<T> message) {
            message.Exceptions.Clear();
            return block.Post(message);
        }

        private static DataflowLinkOptions _propagateOption = new DataflowLinkOptions { 
            PropagateCompletion = true 
        };

        public static void ConnectTo<TOutput>(
            this ISourceBlock<TOutput> source, ITargetBlock<TOutput> target) =>
            source.LinkTo(target, _propagateOption);
    }
}