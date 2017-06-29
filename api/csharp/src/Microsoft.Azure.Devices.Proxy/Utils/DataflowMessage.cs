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

        public override string ToString() => $"{Arg} ({Exceptions.Count})";

        public static IPropagatorBlock<T, DataflowMessage<T>> CreateAdapter() =>
            new TransformBlock<T, DataflowMessage<T>>(arg => new DataflowMessage<T> { Arg = arg });

        public static IPropagatorBlock<T, DataflowMessage<T>> CreateAdapter(ExecutionDataflowBlockOptions options) =>
            new TransformBlock<T, DataflowMessage<T>>(arg => new DataflowMessage<T> { Arg = arg }, options);

    }
}