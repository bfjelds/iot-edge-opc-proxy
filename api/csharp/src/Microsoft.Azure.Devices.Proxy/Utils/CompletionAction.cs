﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace System.Threading.Tasks {
    using System;
    using System.Threading;

    /// <summary>
    /// Ref counted completion action 
    /// </summary>
    public class CompletionAction {

        public CompletionAction(int initialCount, Action action) {
            _counter = initialCount;
            _action = action;
        }

        /// <summary>
        /// End action - runs action if ref count is 0
        /// </summary>
        public void End() {
            if (0 == Interlocked.Decrement(ref _counter)) {
                _action();
                _action = null;
            }
        }

        /// <summary>
        /// Begin action 
        /// </summary>
        public void Begin() {
            Interlocked.Increment(ref _counter);
        }

        private int _counter;
        private Action _action;
    }
}