// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace System.Threading.Tasks.Dataflow {
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A dataflow block input with timeout and last exception
    /// </summary>
    public class DataflowMessage<T> {

        /// <summary> The message input or output content </summary>
        public T Arg { get; set; }

        /// <summary> Exceptions that occurred during processing </summary>
        public LinkedList<Exception> Exceptions { get; set; } = new LinkedList<Exception>();
    }

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

        public static IPropagatorBlock<T, DataflowMessage<T>> DataflowMessageTransformBlock<T>() =>
            new TransformBlock<T, DataflowMessage<T>>(arg => new DataflowMessage<T> { Arg = arg });

        public static IPropagatorBlock<T, DataflowMessage<T>> DataflowMessageTransformBlock<T>(ExecutionDataflowBlockOptions options) =>
            new TransformBlock<T, DataflowMessage<T>>(arg => new DataflowMessage<T> { Arg = arg }, options);
    }
}