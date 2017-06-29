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
    /// Provides a TaskScheduler that provides control over priorities. 
    /// </summary>
    [DebuggerTypeProxy(typeof(QueuedTaskSchedulerDebugView))]
    [DebuggerDisplay("Id={Id}, Queues={DebugQueueCount}, ScheduledTasks = {DebugTaskCount}")]
    public sealed class QueuedTaskScheduler : TaskScheduler, IDisposable {

        public static QueuedTaskScheduler Priority { get; } = new QueuedTaskScheduler();

        /// <summary>
        /// Create default scheduler and queues
        /// </summary>
        static QueuedTaskScheduler() {
        }

        /// <summary>
        /// Default constructor  - using default task scheduler 
        /// </summary>
        public QueuedTaskScheduler() : 
            this(TaskScheduler.Default, 4) { }

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
        public QueuedTaskScheduler(TaskScheduler targetScheduler, 
            int maxConcurrencyLevel) {
            _targetScheduler = targetScheduler ?? throw new ArgumentNullException("underlyingScheduler");
            if (maxConcurrencyLevel < 0) {
                throw new ArgumentOutOfRangeException("concurrencyLevel");
            }
            if (maxConcurrencyLevel == 0 || maxConcurrencyLevel > targetScheduler.MaximumConcurrencyLevel) {
                _concurrencyLevel = targetScheduler.MaximumConcurrencyLevel;
            }
            else {
                _concurrencyLevel = maxConcurrencyLevel;
            }
        }

        /// <summary>
        /// Queue the task to the task queue and check whether we should launch a processing task 
        /// (noting it if we do, so that other threads don't result in queueing up too many tasks).
        /// </summary>
        /// <param name="task">The task to be queued.</param>
        protected override void QueueTask(Task task) {
            if (_disposeCancellation.IsCancellationRequested) {
                throw new ObjectDisposedException(GetType().Name);
            }
            lock (_nonthreadsafeTaskQueue) {
                _nonthreadsafeTaskQueue.Enqueue(task);
                if (_delegatesQueuedOrRunning >= _concurrencyLevel) {
                    return;
                }
                ++_delegatesQueuedOrRunning;
            }

            Task.Factory.StartNew(ProcessPrioritizedAndBatchedTasks, CancellationToken.None, 
                TaskCreationOptions.None, _targetScheduler);
        }


        /// <summary>
        /// Find the next task that should be executed, based on priorities and fairness and the like.
        /// </summary>
        /// <param name="queueForTargetTask">The scheduler associated with the found task.</param>
        /// <returns>The found task, or null if none was found.</returns>
        private void FindNextTask(out Task targetTask, out QueuedTaskSchedulerQueue queueForTargetTask) {
            lock (_queues) {
                foreach (var queue in _queues.Values) {
                    var items = queue._workItems;
                    if (items.Count > 0) {
                        targetTask = items.Dequeue();
                        queueForTargetTask = queue;
                        return;
                    }
                    else if (queue._disposed) { 
                        _queues.Remove(queue._priority);
                    }
                }
            }
            queueForTargetTask = null;
            targetTask = null;
        }

        /// <summary>
        /// Process tasks one at a time in the best order. This will be run in a Task generated 
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

                        if (targetTask == null) {
                            //
                            // If the task is null, it's a placeholder for a task in a round-robin queue.
                            // Find it and run it.
                            //
                            FindNextTask(out targetTask, out QueuedTaskSchedulerQueue queueForTargetTask);
                            if (targetTask != null){
                                if (queueForTargetTask != null) {
                                    queueForTargetTask.ExecuteTask(targetTask);
                                }
                                else {
                                    TryExecuteTask(targetTask);
                                }
                            }
                        }
                        else {
                            TryExecuteTask(targetTask);
                        }
                    }
                }
                finally {
                    //
                    // Now that we think we're done, verify that there really is no more work to do.  
                    // If there's not, highlight that we're now less parallel than we were a moment ago.
                    //
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
        /// Tries to execute a task synchronously on the current thread.
        /// </summary>
        /// <param name="task">The task to execute.</param>
        /// <param name="taskWasPreviouslyQueued">Whether the task was previously queued.</param>
        /// <returns>true if the task was executed; otherwise, false.</returns>
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
            _taskProcessingThread.Value && TryExecuteTask(task);

        /// <summary>
        /// Gets the tasks scheduled to this scheduler.
        /// </summary>
        /// <returns>An enumerable of all tasks queued to this scheduler.</returns>
        protected override IEnumerable<Task> GetScheduledTasks() => 
            _nonthreadsafeTaskQueue.Where(t => t != null).ToList();

        /// <summary>
        /// Gets the maximum concurrency level to use when processing tasks.
        /// </summary>
        public override int MaximumConcurrencyLevel { get => _concurrencyLevel; }

        /// <summary>
        /// Initiates shutdown of the scheduler.
        /// </summary>
        public void Dispose()  => _disposeCancellation.Cancel();

        /// <summary>
        /// Gets a new scheduling queue for this scheduler - creates it if it does not exist.
        /// </summary>
        /// <param name="priority">The priority level for the new queue.</param>
        /// <returns>The newly created and activated queue at the specified priority.</returns>
        public TaskScheduler GetQueue(int priority = 0) {
            lock (_queues) {
                if (!_queues.TryGetValue(priority, out QueuedTaskSchedulerQueue queue)) {
                    queue = new QueuedTaskSchedulerQueue(priority, this);
                    _queues.Add(priority, queue);
                }
                return queue;
            }
        }

        public TaskScheduler this[int priority] => GetQueue(priority);


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
                lock (_pool._queues) {
                    _workItems.Enqueue(task);
                }
                _pool.QueueTask(null);
            }

            /// <summary>
            /// Tries to execute a task synchronously on the current thread.
            /// If we're currently processing another task on this thread, try running it inline.
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
            public void Dispose() =>  _disposed = true;

            /// <summary>A debug view for the queue.</summary>
            private sealed class QueuedTaskSchedulerQueueDebugView {
                public QueuedTaskSchedulerQueueDebugView(QueuedTaskSchedulerQueue queue) => _queue = queue;
                public int Priority { get => _queue._priority; }
                public int Id { get => _queue.Id; }
                public IEnumerable<Task> ScheduledTasks { get => _queue.GetScheduledTasks();  }
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
            public IEnumerable<Task> ScheduledTasks { get => _scheduler.GetScheduledTasks(); }
            public IEnumerable<TaskScheduler> Queues { get => _scheduler._queues.Values; }
            private QueuedTaskScheduler _scheduler;
        }

        // A sorted list of queues
        private readonly SortedList<int, QueuedTaskSchedulerQueue> _queues = 
            new SortedList<int, QueuedTaskSchedulerQueue>();

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