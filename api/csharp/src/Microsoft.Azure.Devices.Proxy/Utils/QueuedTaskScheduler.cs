//--------------------------------------------------------------------------
// 
//  Copyright (c) Microsoft Corporation.  All rights reserved. 
// 
//  File: QueuedTaskScheduler.cs
//
//--------------------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace System.Threading.Tasks {

    /// <summary>
    /// Provides a TaskScheduler that provides control over priorities, fairness, and the underlying threads utilized.
    /// </summary>
    [DebuggerTypeProxy(typeof(QueuedTaskSchedulerDebugView))]
    [DebuggerDisplay("Id={Id}, Queues={DebugQueueCount}, ScheduledTasks = {DebugTaskCount}")]
    public sealed class QueuedTaskScheduler : TaskScheduler, IDisposable {

        /// <summary>
        /// Default constructor  - using default task scheduler 
        /// </summary>
        public QueuedTaskScheduler() : 
            this(TaskScheduler.Default, 0) { }

        /// <summary>
        /// Initializes the scheduler.
        /// </summary>
        /// <param name="targetScheduler">
        /// The target underlying scheduler onto which this sceduler's work is queued.
        /// </param>
        public QueuedTaskScheduler(TaskScheduler targetScheduler) :
            this(targetScheduler, targetScheduler.MaximumConcurrencyLevel) { }

        /// <summary>
        /// Initializes the scheduler.
        /// </summary>
        /// <param name="targetScheduler">The underlying scheduler</param>
        /// <param name="maxConcurrencyLevel">Max degree of concurrency allowed.</param>
        public QueuedTaskScheduler(TaskScheduler targetScheduler, int maxConcurrencyLevel) {

            _targetScheduler = targetScheduler ?? throw new ArgumentNullException("underlyingScheduler");
            _nonthreadsafeTaskQueue = new Queue<Task>();

            if (maxConcurrencyLevel < 0) {
                throw new ArgumentOutOfRangeException("concurrencyLevel");
            }


            // If 0, use the number of logical processors.  But make sure whatever value we pick
            // is not greater than the degree of parallelism allowed by the underlying scheduler.
            _concurrencyLevel = maxConcurrencyLevel != 0 ? maxConcurrencyLevel : Environment.ProcessorCount;
            if (targetScheduler.MaximumConcurrencyLevel > 0 && targetScheduler.MaximumConcurrencyLevel < _concurrencyLevel) {
                _concurrencyLevel = targetScheduler.MaximumConcurrencyLevel;
            }
        }


        /// <summary>Queues a task to the scheduler.</summary>
        /// <param name="task">The task to be queued.</param>
        protected override void QueueTask(Task task) {
            // If we've been disposed, no one should be queueing
            if (_disposeCancellation.IsCancellationRequested) throw new ObjectDisposedException(GetType().Name);
            // Queue the task and check whether we should launch a processing
            // task (noting it if we do, so that other threads don't result
            // in queueing up too many).
            bool launchTask = false;
            lock (_nonthreadsafeTaskQueue) {
                _nonthreadsafeTaskQueue.Enqueue(task);
                if (_delegatesQueuedOrRunning < _concurrencyLevel) {
                    ++_delegatesQueuedOrRunning;
                    launchTask = true;
                }
            }

            // If necessary, start processing asynchronously
            if (launchTask) {
                Task.Factory.StartNew(ProcessPrioritizedAndBatchedTasks,
                    CancellationToken.None, TaskCreationOptions.None, _targetScheduler);
            }
        }


        /// <summary>
        /// Find the next task that should be executed, based on priorities and fairness and the like.
        /// </summary>
        /// <param name="queueForTargetTask">The scheduler associated with the found task.</param>
        /// <returns>The found task, or null if none was found.</returns>
        private Task FindNextTask(out QueuedTaskSchedulerQueue queueForTargetTask) {
            queueForTargetTask = null;
            lock (_queueGroups) {
                // Look through each of our queue groups in sorted order.
                // This ordering is based on the priority of the queues.
                foreach (var queueGroup in _queueGroups) {
                    var queues = queueGroup.Value;
                    // Within each group, iterate through the queues in a round-robin
                    // fashion.  Every time we iterate again and successfully find a task, 
                    // we'll start in the next location in the group.
                    foreach (int i in queues.CreateSearchOrder()) {
                        queueForTargetTask = queues[i];
                        var items = queueForTargetTask._workItems;
                        if (items.Count > 0) {
                            var targetTask = items.Dequeue();
                            if (queueForTargetTask._disposed && items.Count == 0) {
                                RemoveQueue_NeedsLock(queueForTargetTask);
                            }
                            queues.NextQueueIndex = (queues.NextQueueIndex + 1) % queueGroup.Value.Count;
                            return targetTask;
                        }
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// Process tasks one at a time in the best order. This should be run in a Task generated 
        /// by QueueTask. 
        /// </summary>
        private void ProcessPrioritizedAndBatchedTasks() {
            bool continueProcessing = true;
            while (!_disposeCancellation.IsCancellationRequested && continueProcessing) {
                try {
                    // Note that we're processing tasks on this thread
                    _taskProcessingThread.Value = true;

                    // Until there are no more tasks to process
                    while (!_disposeCancellation.IsCancellationRequested) {
                        // Try to get the next task.  If there aren't any more, we're done.
                        Task targetTask;
                        lock (_nonthreadsafeTaskQueue) {
                            if (_nonthreadsafeTaskQueue.Count == 0) {
                                break;
                            }
                            targetTask = _nonthreadsafeTaskQueue.Dequeue();
                        }

                        // If the task is null, it's a placeholder for a task in the round-robin queues.
                        // Find the next one that should be processed.
                        QueuedTaskSchedulerQueue queueForTargetTask = null;
                        if (targetTask == null) {
                            targetTask = FindNextTask(out queueForTargetTask);
                        }

                        // Now if we finally have a task, run it.  If the task
                        // was associated with one of the round-robin schedulers, we need to use it
                        // as a thunk to execute its task.
                        if (targetTask != null) {
                            if (queueForTargetTask != null) {
                                queueForTargetTask.ExecuteTask(targetTask);
                            }
                            else {
                                TryExecuteTask(targetTask);
                            }
                        }
                    }
                }
                finally {
                    // Now that we think we're done, verify that there really is
                    // no more work to do.  If there's not, highlight
                    // that we're now less parallel than we were a moment ago.
                    lock (_nonthreadsafeTaskQueue) {
                        if (_nonthreadsafeTaskQueue.Count == 0) {
                            _delegatesQueuedOrRunning--;
                            continueProcessing = false;
                            _taskProcessingThread.Value = false;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Notifies the pool that there's a new item to be executed in one of
        /// the round-robin queues.
        /// </summary>
        private void NotifyNewWorkItem() => QueueTask(null); 

        /// <summary>Tries to execute a task synchronously on the current thread.</summary>
        /// <param name="task">The task to execute.</param>
        /// <param name="taskWasPreviouslyQueued">Whether the task was previously queued.</param>
        /// <returns>true if the task was executed; otherwise, false.</returns>
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
            _taskProcessingThread.Value && TryExecuteTask(task);

        /// <summary>Gets the tasks scheduled to this scheduler.</summary>
        /// <returns>An enumerable of all tasks queued to this scheduler.</returns>
        /// <remarks>This does not include the tasks on sub-schedulers.  
        /// Those will be retrieved by the debugger separately.</remarks>
        protected override IEnumerable<Task> GetScheduledTasks() => 
            _nonthreadsafeTaskQueue.Where(t => t != null).ToList();

        /// <summary>Gets the maximum concurrency level to use when processing tasks.</summary>
        public override int MaximumConcurrencyLevel { get => _concurrencyLevel; }

        /// <summary>Initiates shutdown of the scheduler.</summary>
        public void Dispose()  => _disposeCancellation.Cancel();

        /// <summary>A group of queues a the same priority level.</summary>
        private class QueueGroup : List<QueuedTaskSchedulerQueue> {
            /// <summary>The starting index for the next round-robin traversal.</summary>
            public int NextQueueIndex = 0;

            /// <summary>Creates a search order through this group.</summary>
            /// <returns>An enumerable of indices for this group.</returns>
            public IEnumerable<int> CreateSearchOrder() {
                for (int i = NextQueueIndex; i < Count; i++) {
                    yield return i;
                }
                for (int i = 0; i < NextQueueIndex; i++) {
                    yield return i;
                }
            }
        }

        /// <summary>
        /// Creates and activates a new scheduling queue for this scheduler.
        /// </summary>
        /// <param name="priority">The priority level for the new queue.</param>
        /// <returns>The newly created and activated queue at the specified priority.</returns>
        public TaskScheduler ActivateNewQueue(int priority = 0) {
            // Create the queue
            var createdQueue = new QueuedTaskSchedulerQueue(priority, this);

            // Add the queue to the appropriate queue group based on priority
            lock (_queueGroups) {
                if (!_queueGroups.TryGetValue(priority, out QueueGroup list)) {
                    list = new QueueGroup();
                    _queueGroups.Add(priority, list);
                }
                list.Add(createdQueue);
            }

            // Hand the new queue back
            return createdQueue;
        }

        /// <summary>Removes a scheduler from the group.</summary>
        /// <param name="queue">The scheduler to be removed.</param>
        private void RemoveQueue_NeedsLock(QueuedTaskSchedulerQueue queue) {
            
            // Find the group that contains the queue and the queue's index within the group
            var queueGroup = _queueGroups[queue._priority];
            int index = queueGroup.IndexOf(queue);

            // We're about to remove the queue, so adjust the index of the next
            // round-robin starting location if it'll be affected by the removal
            if (queueGroup.NextQueueIndex >= index) {
                queueGroup.NextQueueIndex--;
            }

            // Remove it
            queueGroup.RemoveAt(index);
        }

        /// <summary>Provides a scheduling queue associatd with a QueuedTaskScheduler.</summary>
        [DebuggerDisplay("QueuePriority = {_priority}, WaitingTasks = {WaitingTasks}")]
        [DebuggerTypeProxy(typeof(QueuedTaskSchedulerQueueDebugView))]
        private sealed class QueuedTaskSchedulerQueue : TaskScheduler, IDisposable {

            /// <summary>Initializes the queue.</summary>
            /// <param name="priority">The priority associated with this queue.</param>
            /// <param name="pool">The scheduler with which this queue is associated.</param>
            internal QueuedTaskSchedulerQueue(int priority, QueuedTaskScheduler pool) {
                _priority = priority;
                _pool = pool;
                _workItems = new Queue<Task>();
            }

            /// <summary>Gets the number of tasks waiting in this scheduler.</summary>
            internal int WaitingTasks { get => _workItems.Count; }

            /// <summary>Gets the tasks scheduled to this scheduler.</summary>
            /// <returns>An enumerable of all tasks queued to this scheduler.</returns>
            protected override IEnumerable<Task> GetScheduledTasks() => _workItems.ToList();

            /// <summary>Queues a task to the scheduler.</summary>
            /// <param name="task">The task to be queued.</param>
            protected override void QueueTask(Task task) {
                if (_disposed) {
                    throw new ObjectDisposedException(GetType().Name);
                }

                // Queue up the task locally to this queue, and then notify
                // the parent scheduler that there's work available
                lock (_pool._queueGroups) {
                    _workItems.Enqueue(task);
                }
                _pool.NotifyNewWorkItem();
            }

            /// <summary>
            /// Tries to execute a task synchronously on the current thread.
            /// If we're using our own threads and if this is being called from one of them,
            /// or if we're currently processing another task on this thread, try running it inline.
            /// </summary>
            /// <param name="task">The task to execute.</param>
            /// <param name="taskWasPreviouslyQueued">Whether the task was previously queued.</param>
            /// <returns>true if the task was executed; otherwise, false.</returns>
            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
                _taskProcessingThread.Value && TryExecuteTask(task);

            /// <summary>
            /// Runs the specified ask.
            /// </summary>
            /// <param name="task">The task to execute.</param>
            internal void ExecuteTask(Task task) => TryExecuteTask(task); 

            /// <summary>Gets the maximum concurrency level to use when processing tasks.</summary>
            public override int MaximumConcurrencyLevel { get => _pool.MaximumConcurrencyLevel; }

            /// <summary>
            /// Signals that the queue should be removed from the 
            /// scheduler as soon as the queue is empty.
            /// </summary>
            public void Dispose() {
                if (!_disposed) {
                    lock (_pool._queueGroups) {
                        // We only remove the queue if it's empty.  If it's not empty,
                        // we still mark it as disposed, and the associated QueuedTaskScheduler
                        // will remove the queue when its count hits 0 and its _disposed is true.
                        if (_workItems.Count == 0) {
                            _pool.RemoveQueue_NeedsLock(this);
                        }
                    }
                    _disposed = true;
                }
            }

            /// <summary>A debug view for the queue.</summary>
            private sealed class QueuedTaskSchedulerQueueDebugView {
                public QueuedTaskSchedulerQueueDebugView(QueuedTaskSchedulerQueue queue) => _queue = queue;
                /// <summary>Gets the priority of this queue in its associated scheduler.</summary>
                public int Priority { get => _queue._priority; }
                /// <summary>Gets the ID of this scheduler.</summary>
                public int Id { get => _queue.Id; }
                /// <summary>Gets all of the tasks scheduled to this queue.</summary>
                public IEnumerable<Task> ScheduledTasks { get => _queue.GetScheduledTasks();  }
                /// <summary>Gets the QueuedTaskScheduler with which this queue is associated.</summary>
                public QueuedTaskScheduler AssociatedScheduler { get => _queue._pool; }
                private readonly QueuedTaskSchedulerQueue _queue;
            }

            private readonly QueuedTaskScheduler _pool;
            internal readonly Queue<Task> _workItems;
            internal bool _disposed;
            internal int _priority;
        }

        /// <summary> Debugger view </summary>
        private class QueuedTaskSchedulerDebugView {
            public QueuedTaskSchedulerDebugView(QueuedTaskScheduler scheduler) => _scheduler = scheduler;

            /// <summary>Gets all of the Tasks queued to the scheduler directly.</summary>
            public IEnumerable<Task> ScheduledTasks {
                get => _scheduler._nonthreadsafeTaskQueue.Where(t => t != null).ToList();
            }

            /// <summary>Gets the prioritized and fair queues.</summary>
            public IEnumerable<TaskScheduler> Queues {
                get {
                    var queues = new List<TaskScheduler>();
                    foreach (var group in _scheduler._queueGroups) {
                        queues.AddRange(group.Value);
                    }
                    return queues;
                }
            }
            private QueuedTaskScheduler _scheduler;
        }

        // A sorted list of round-robin queue lists. Tasks with the smallest priority value
        // are preferred.  Priority groups are round-robin'd through in order of priority.
        private readonly SortedList<int, QueueGroup> _queueGroups = new SortedList<int, QueueGroup>();
        private readonly CancellationTokenSource _disposeCancellation = new CancellationTokenSource();
        /// The maximum allowed concurrency level of this scheduler.
        private readonly int _concurrencyLevel;
        /// Whether we're processing tasks on the current thread.
        private static ThreadLocal<bool> _taskProcessingThread = new ThreadLocal<bool>();
        /// The scheduler onto which actual work is scheduled.
        private readonly TaskScheduler _targetScheduler;
        /// The queue of tasks to process
        private readonly Queue<Task> _nonthreadsafeTaskQueue = new Queue<Task>();
        // The number of Tasks that have been queued or that are running 
        private int _delegatesQueuedOrRunning = 0;
    }
}