namespace WebSocketSharp
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A mutual exclusion lock that is compatible with async. Note that this lock is <b>not</b> recursive!
    /// </summary>
    [DebuggerDisplay("Id = {Id}, Taken = {_taken}")]
    [DebuggerTypeProxy(typeof(DebugView))]
    internal sealed class AsyncLock
    {
        /// <summary>
        /// Whether the lock is taken by a task.
        /// </summary>
        private bool _taken;

        /// <summary>
        /// The queue of TCSs that other tasks are awaiting to acquire the lock.
        /// </summary>
        private readonly IAsyncWaitQueue<IDisposable> _queue;

        /// <summary>
        /// A task that is completed with the key object for this lock.
        /// </summary>
        private readonly Task<IDisposable> _cachedKeyTask;

        /// <summary>
        /// The semi-unique identifier for this instance. This is 0 if the id has not yet been created.
        /// </summary>
        private int _id;

        /// <summary>
        /// The object used for mutual exclusion.
        /// </summary>
        private readonly object _mutex;

        /// <summary>
        /// Creates a new async-compatible mutual exclusion lock.
        /// </summary>
        public AsyncLock()
            : this(new DefaultAsyncWaitQueue<IDisposable>())
        {
        }

        /// <summary>
        /// Creates a new async-compatible mutual exclusion lock using the specified wait queue.
        /// </summary>
        /// <param name="queue">The wait queue used to manage waiters.</param>
        public AsyncLock(IAsyncWaitQueue<IDisposable> queue)
        {
            _queue = queue;
            _cachedKeyTask = Task.FromResult<IDisposable>(new Key(this));
            _mutex = new object();
        }

        /// <summary>
        /// Gets a semi-unique identifier for this asynchronous lock.
        /// </summary>
        public int Id
        {
            get { return IdManager<AsyncLock>.GetId(ref _id); }
        }

        /// <summary>
        /// Asynchronously acquires the lock. Returns a disposable that releases the lock when disposed.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
        /// <returns>A disposable that releases the lock when disposed.</returns>
        public AwaitableDisposable<IDisposable> LockAsync(CancellationToken cancellationToken)
        {
            Task<IDisposable> ret;
            lock (_mutex)
            {
                if (!_taken)
                {
                    // If the lock is available, take it immediately.
                    _taken = true;
                    ret = _cachedKeyTask;
                }
                else
                {
                    // Wait for the lock to become available or cancellation.
                    ret = _queue.Enqueue(_mutex, cancellationToken);
                }

                //Enlightenment.Trace.AsyncLock_TrackLock(this, ret);
            }

            return new AwaitableDisposable<IDisposable>(ret);
        }

        /// <summary>
        /// Asynchronously acquires the lock. Returns a disposable that releases the lock when disposed.
        /// </summary>
        /// <returns>A disposable that releases the lock when disposed.</returns>
        public AwaitableDisposable<IDisposable> LockAsync()
        {
            return LockAsync(CancellationToken.None);
        }

        /// <summary>
        /// Releases the lock.
        /// </summary>
        internal void ReleaseLock()
        {
            IDisposable finish = null;
            lock (_mutex)
            {
                //Enlightenment.Trace.AsyncLock_Unlocked(this);
                if (_queue.IsEmpty)
                    _taken = false;
                else
                    finish = _queue.Dequeue(_cachedKeyTask.Result);
            }
            finish?.Dispose();
        }

        /// <summary>
        /// The disposable which releases the lock.
        /// </summary>
        private struct Key : IDisposable
        {
            /// <summary>
            /// The lock to release.
            /// </summary>
            private readonly AsyncLock _asyncLock;

            /// <summary>
            /// Creates the key for a lock.
            /// </summary>
            /// <param name="asyncLock">The lock to release. May not be <c>null</c>.</param>
            public Key(AsyncLock asyncLock)
            {
                _asyncLock = asyncLock;
            }

            /// <summary>
            /// Release the lock.
            /// </summary>
            public void Dispose()
            {
                _asyncLock.ReleaseLock();
            }
        }

        // ReSharper disable UnusedMember.Local
        [DebuggerNonUserCode]
        private sealed class DebugView
        {
            private readonly AsyncLock _mutex;

            public DebugView(AsyncLock mutex)
            {
                _mutex = mutex;
            }

            public int Id { get { return _mutex.Id; } }

            public bool Taken { get { return _mutex._taken; } }

            public IAsyncWaitQueue<IDisposable> WaitQueue { get { return _mutex._queue; } }
        }
        // ReSharper restore UnusedMember.Local
    }

    internal struct AwaitableDisposable<T> where T : IDisposable
    {
        private readonly Task<T> _task;

        public AwaitableDisposable(Task<T> task)
        {
            _task = task;
        }

        public Task<T> AsTask()
        {
            return _task;
        }

        public static implicit operator Task<T>(AwaitableDisposable<T> source)
        {
            return source.AsTask();
        }

        public TaskAwaiter<T> GetAwaiter()
        {
            return _task.GetAwaiter();
        }

        public ConfiguredTaskAwaitable<T> ConfigureAwait(bool continueOnCapturedContext)
        {
            return _task.ConfigureAwait(continueOnCapturedContext);
        }
    }

    /// <summary>
    /// Allocates Ids for instances on demand. 0 is an invalid/unassigned Id. Ids may be non-unique in very long-running systems. This is similar to the Id system used by <see cref="System.Threading.Tasks.Task"/> and <see cref="System.Threading.Tasks.TaskScheduler"/>.
    /// </summary>
    /// <typeparam name="TTag">The type for which ids are generated.</typeparam>
    // ReSharper disable UnusedTypeParameter
    internal static class IdManager<TTag>
    // ReSharper restore UnusedTypeParameter
    {
        /// <summary>
        /// The last id generated for this type. This is 0 if no ids have been generated.
        /// </summary>
// ReSharper disable StaticFieldInGenericType
        private static int _lastId;
        // ReSharper restore StaticFieldInGenericType

        /// <summary>
        /// Returns the id, allocating it if necessary.
        /// </summary>
        /// <param name="id">A reference to the field containing the id.</param>
        public static int GetId(ref int id)
        {
            // If the Id has already been assigned, just use it.
            if (id != 0)
                return id;

            // Determine the new Id without modifying "id", since other threads may also be determining the new Id at the same time.
            int newId;

            // The Increment is in a while loop to ensure we get a non-zero Id:
            //  If we are incrementing -1, then we want to skip over 0.
            //  If there are tons of Id allocations going on, we want to skip over 0 no matter how many times we get it.
            do
            {
                newId = Interlocked.Increment(ref _lastId);
            } while (newId == 0);

            // Update the Id unless another thread already updated it.
            Interlocked.CompareExchange(ref id, newId, 0);

            // Return the current Id, regardless of whether it's our new Id or a new Id from another thread.
            return id;
        }
    }

    /// <summary>
    /// A collection of cancelable <see cref="TaskCompletionSource{T}"/> instances. Implementations must be threadsafe <b>and</b> must work correctly if the caller is holding a lock.
    /// </summary>
    /// <typeparam name="T">The type of the results. If this isn't needed, use <see cref="Object"/>.</typeparam>
    internal interface IAsyncWaitQueue<T>
    {
        /// <summary>
        /// Gets whether the queue is empty.
        /// </summary>
        bool IsEmpty { get; }

        /// <summary>
        /// Creates a new entry and queues it to this wait queue. The returned task must support both synchronous and asynchronous waits.
        /// </summary>
        /// <returns>The queued task.</returns>
        Task<T> Enqueue();

        /// <summary>
        /// Removes a single entry in the wait queue. Returns a disposable that completes that entry.
        /// </summary>
        /// <param name="result">The result used to complete the wait queue entry. If this isn't needed, use <c>default(T)</c>.</param>
        IDisposable Dequeue(T result = default(T));

        /// <summary>
        /// Removes all entries in the wait queue. Returns a disposable that completes all entries.
        /// </summary>
        /// <param name="result">The result used to complete the wait queue entries. If this isn't needed, use <c>default(T)</c>.</param>
        IDisposable DequeueAll(T result = default(T));

        /// <summary>
        /// Attempts to remove an entry from the wait queue. Returns a disposable that cancels the entry.
        /// </summary>
        /// <param name="task">The task to cancel.</param>
        /// <returns>A value indicating whether the entry was found and canceled.</returns>
        IDisposable TryCancel(Task task);

        /// <summary>
        /// Removes all entries from the wait queue. Returns a disposable that cancels all entries.
        /// </summary>
        IDisposable CancelAll();
    }

    /// <summary>
    /// Provides extension methods for wait queues.
    /// </summary>
    internal static class AsyncWaitQueueExtensions
    {
        /// <summary>
        /// Creates a new entry and queues it to this wait queue. If the cancellation token is already canceled, this method immediately returns a canceled task without modifying the wait queue.
        /// </summary>
        /// <param name="this">The wait queue.</param>
        /// <param name="token">The token used to cancel the wait.</param>
        /// <returns>The queued task.</returns>
        [Obsolete("Use the Enqueue overload that takes a synchronization object.")]
        public static Task<T> Enqueue<T>(this IAsyncWaitQueue<T> @this, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return TaskConstants<T>.Canceled;

            var ret = @this.Enqueue();
            if (!token.CanBeCanceled)
                return ret;

            var registration = token.Register(() => @this.TryCancel(ret).Dispose(), useSynchronizationContext: false);
            ret.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return ret;
        }

        /// <summary>
        /// Creates a new entry and queues it to this wait queue. If the cancellation token is already canceled, this method immediately returns a canceled task without modifying the wait queue.
        /// </summary>
        /// <param name="this">The wait queue.</param>
        /// <param name="syncObject">A synchronization object taken while cancelling the entry.</param>
        /// <param name="token">The token used to cancel the wait.</param>
        /// <returns>The queued task.</returns>
        public static Task<T> Enqueue<T>(this IAsyncWaitQueue<T> @this, object syncObject, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return TaskConstants<T>.Canceled;

            var ret = @this.Enqueue();
            if (!token.CanBeCanceled)
                return ret;

            var registration = token.Register(() =>
            {
                IDisposable finish;
                lock (syncObject)
                    finish = @this.TryCancel(ret);
                finish.Dispose();
            }, useSynchronizationContext: false);
            ret.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return ret;
        }
    }

    /// <summary>
    /// Provides completed task constants.
    /// </summary>
    /// <typeparam name="T">The type of the task result.</typeparam>
    public static class TaskConstants<T>
    {
        private static readonly Task<T> defaultValue = Task.FromResult(default(T));

        private static readonly Task<T> never = new TaskCompletionSource<T>().Task;

        private static readonly Task<T> canceled = CanceledTask();

        private static Task<T> CanceledTask()
        {
            var tcs = new TaskCompletionSource<T>();
            tcs.SetCanceled();
            return tcs.Task;
        }

        /// <summary>
        /// A task that has been completed with the default value of <typeparamref name="T"/>.
        /// </summary>
        public static Task<T> Default
        {
            get
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// A <see cref="Task"/> that will never complete.
        /// </summary>
        public static Task<T> Never
        {
            get
            {
                return never;
            }
        }

        /// <summary>
        /// A task that has been canceled.
        /// </summary>
        public static Task<T> Canceled
        {
            get
            {
                return canceled;
            }
        }
    }

    /// <summary>
    /// The default wait queue implementation, which uses a double-ended queue.
    /// </summary>
    /// <typeparam name="T">The type of the results. If this isn't needed, use <see cref="Object"/>.</typeparam>
    [DebuggerDisplay("Count = {Count}")]
    [DebuggerTypeProxy(typeof(DefaultAsyncWaitQueue<>.DebugView))]
    internal sealed class DefaultAsyncWaitQueue<T> : IAsyncWaitQueue<T>
    {
        private readonly Deque<TaskCompletionSource<T>> _queue = new Deque<TaskCompletionSource<T>>();

        private int Count
        {
            get { lock (_queue) { return _queue.Count; } }
        }

        bool IAsyncWaitQueue<T>.IsEmpty
        {
            get { return Count == 0; }
        }

        Task<T> IAsyncWaitQueue<T>.Enqueue()
        {
            var tcs = new TaskCompletionSource<T>();
            lock (_queue)
                _queue.AddToBack(tcs);
            return tcs.Task;
        }

        IDisposable IAsyncWaitQueue<T>.Dequeue(T result)
        {
            TaskCompletionSource<T> tcs;
            lock (_queue)
                tcs = _queue.RemoveFromFront();
            return new CompleteDisposable(result, tcs);
        }

        IDisposable IAsyncWaitQueue<T>.DequeueAll(T result)
        {
            TaskCompletionSource<T>[] taskCompletionSources;
            lock (_queue)
            {
                taskCompletionSources = _queue.ToArray();
                _queue.Clear();
            }
            return new CompleteDisposable(result, taskCompletionSources);
        }

        IDisposable IAsyncWaitQueue<T>.TryCancel(Task task)
        {
            TaskCompletionSource<T> tcs = null;
            lock (_queue)
            {
                for (int i = 0; i != _queue.Count; ++i)
                {
                    if (_queue[i].Task == task)
                    {
                        tcs = _queue[i];
                        _queue.RemoveAt(i);
                        break;
                    }
                }
            }
            if (tcs == null)
                return new CancelDisposable();
            return new CancelDisposable(tcs);
        }

        IDisposable IAsyncWaitQueue<T>.CancelAll()
        {
            TaskCompletionSource<T>[] taskCompletionSources;
            lock (_queue)
            {
                taskCompletionSources = _queue.ToArray();
                _queue.Clear();
            }
            return new CancelDisposable(taskCompletionSources);
        }

        private sealed class CancelDisposable : IDisposable
        {
            private readonly TaskCompletionSource<T>[] _taskCompletionSources;

            public CancelDisposable(params TaskCompletionSource<T>[] taskCompletionSources)
            {
                _taskCompletionSources = taskCompletionSources;
            }

            public void Dispose()
            {
                foreach (var cts in _taskCompletionSources)
                    cts.TrySetCanceled();
            }
        }

        private sealed class CompleteDisposable : IDisposable
        {
            private readonly TaskCompletionSource<T>[] _taskCompletionSources;
            private readonly T _result;

            public CompleteDisposable(T result, params TaskCompletionSource<T>[] taskCompletionSources)
            {
                _result = result;
                _taskCompletionSources = taskCompletionSources;
            }

            public void Dispose()
            {
                foreach (var cts in _taskCompletionSources)
                    cts.TrySetResult(_result);
            }
        }

        [DebuggerNonUserCode]
        internal sealed class DebugView
        {
            private readonly DefaultAsyncWaitQueue<T> _queue;

            public DebugView(DefaultAsyncWaitQueue<T> queue)
            {
                _queue = queue;
            }

            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public Task<T>[] Tasks
            {
                get { return _queue._queue.Select(x => x.Task).ToArray(); }
            }
        }
    }

    /// <summary>
    /// An async-compatible auto-reset event.
    /// </summary>
    [DebuggerDisplay("Id = {Id}, IsSet = {_set}")]
    [DebuggerTypeProxy(typeof(DebugView))]
    internal sealed class AsyncAutoResetEvent
    {
        /// <summary>
        /// The queue of TCSs that other tasks are awaiting.
        /// </summary>
        private readonly IAsyncWaitQueue<bool> _queue;

        /// <summary>
        /// The current state of the event.
        /// </summary>
        private bool _set;

        /// <summary>
        /// The semi-unique identifier for this instance. This is 0 if the id has not yet been created.
        /// </summary>
        private int _id;

        /// <summary>
        /// The object used for mutual exclusion.
        /// </summary>
        private readonly object _mutex;

        /// <summary>
        /// Creates an async-compatible auto-reset event.
        /// </summary>
        /// <param name="set">Whether the auto-reset event is initially set or unset.</param>
        /// <param name="queue">The wait queue used to manage waiters.</param>
        public AsyncAutoResetEvent(bool set, IAsyncWaitQueue<bool> queue)
        {
            _queue = queue;
            _set = set;
            _mutex = new object();
            //if (set)
            //    Enlightenment.Trace.AsyncAutoResetEvent_Set(this);
        }

        /// <summary>
        /// Creates an async-compatible auto-reset event.
        /// </summary>
        /// <param name="set">Whether the auto-reset event is initially set or unset.</param>
        public AsyncAutoResetEvent(bool set)
            : this(set, new DefaultAsyncWaitQueue<bool>())
        {
        }

        /// <summary>
        /// Creates an async-compatible auto-reset event that is initially unset.
        /// </summary>
        public AsyncAutoResetEvent()
          : this(false, new DefaultAsyncWaitQueue<bool>())
        {
        }

        /// <summary>
        /// Gets a semi-unique identifier for this asynchronous auto-reset event.
        /// </summary>
        public int Id
        {
            get { return IdManager<AsyncAutoResetEvent>.GetId(ref _id); }
        }

        /// <summary>
        /// Whether this event is currently set. This member is seldom used; code using this member has a high possibility of race conditions.
        /// </summary>
        public bool IsSet
        {
            get { lock (_mutex) return _set; }
        }

        /// <summary>
        /// Asynchronously waits for this event to be set. If the event is set, this method will auto-reset it and return immediately, even if the cancellation token is already signalled. If the wait is canceled, then it will not auto-reset this event.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token used to cancel this wait.</param>
        public Task<bool> WaitAsync(CancellationToken cancellationToken)
        {
            Task<bool> ret;
            lock (_mutex)
            {
                if (_set)
                {
                    _set = false;
                    ret = Task.FromResult(true);
                }
                else
                {
                    ret = _queue.Enqueue(_mutex, cancellationToken);
                }
                //Enlightenment.Trace.AsyncAutoResetEvent_TrackWait(this, ret);
            }

            return ret;
        }

        /// <summary>
        /// Asynchronously waits for this event to be set. If the event is set, this method will auto-reset it and return immediately.
        /// </summary>
        public Task WaitAsync()
        {
            return WaitAsync(CancellationToken.None);
        }

        /// <summary>
        /// Sets the event, atomically completing a task returned by <see cref="o:WaitAsync"/>. If the event is already set, this method does nothing.
        /// </summary>
        public void Set()
        {
            IDisposable finish = null;
            lock (_mutex)
            {
                //Enlightenment.Trace.AsyncAutoResetEvent_Set(this);
                if (_queue.IsEmpty)
                    _set = true;
                else
                    finish = _queue.Dequeue();
            }
            if (finish != null)
                finish.Dispose();
        }

        // ReSharper disable UnusedMember.Local
        [DebuggerNonUserCode]
        private sealed class DebugView
        {
            private readonly AsyncAutoResetEvent _are;

            public DebugView(AsyncAutoResetEvent are)
            {
                _are = are;
            }

            public int Id { get { return _are.Id; } }

            public bool IsSet { get { return _are._set; } }

            public IAsyncWaitQueue<bool> WaitQueue { get { return _are._queue; } }
        }
        // ReSharper restore UnusedMember.Local
    }

    /// <summary>
    /// An async-compatible condition variable. This type uses Mesa-style semantics (the notifying tasks do not yield).
    /// </summary>
    [DebuggerDisplay("Id = {Id}, AsyncLockId = {_asyncLock.Id}")]
    [DebuggerTypeProxy(typeof(DebugView))]
    internal sealed class AsyncConditionVariable
    {
        /// <summary>
        /// The lock associated with this condition variable.
        /// </summary>
        private readonly AsyncLock _asyncLock;

        /// <summary>
        /// The queue of waiting tasks.
        /// </summary>
        private readonly IAsyncWaitQueue<object> _queue;

        /// <summary>
        /// The semi-unique identifier for this instance. This is 0 if the id has not yet been created.
        /// </summary>
        private int _id;

        /// <summary>
        /// The object used for mutual exclusion.
        /// </summary>
        private readonly object _mutex;

        /// <summary>
        /// Creates an async-compatible condition variable associated with an async-compatible lock.
        /// </summary>
        /// <param name="asyncLock">The lock associated with this condition variable.</param>
        /// <param name="queue">The wait queue used to manage waiters.</param>
        public AsyncConditionVariable(AsyncLock asyncLock, IAsyncWaitQueue<object> queue)
        {
            _asyncLock = asyncLock;
            _queue = queue;
            _mutex = new object();
        }

        /// <summary>
        /// Creates an async-compatible condition variable associated with an async-compatible lock.
        /// </summary>
        /// <param name="asyncLock">The lock associated with this condition variable.</param>
        public AsyncConditionVariable(AsyncLock asyncLock)
            : this(asyncLock, new DefaultAsyncWaitQueue<object>())
        {
        }

        /// <summary>
        /// Gets a semi-unique identifier for this asynchronous condition variable.
        /// </summary>
        public int Id
        {
            get { return IdManager<AsyncConditionVariable>.GetId(ref _id); }
        }

        /// <summary>
        /// Sends a signal to a single task waiting on this condition variable. The associated lock MUST be held when calling this method, and it will still be held when this method returns.
        /// </summary>
        public void Notify()
        {
            IDisposable finish = null;
            lock (_mutex)
            {
                //Enlightenment.Trace.AsyncConditionVariable_NotifyOne(this, _asyncLock);
                if (!_queue.IsEmpty)
                    finish = _queue.Dequeue();
            }
            if (finish != null)
                finish.Dispose();
        }

        /// <summary>
        /// Sends a signal to all tasks waiting on this condition variable. The associated lock MUST be held when calling this method, and it will still be held when this method returns.
        /// </summary>
        public void NotifyAll()
        {
            IDisposable finish;
            lock (_mutex)
            {
                //Enlightenment.Trace.AsyncConditionVariable_NotifyAll(this, _asyncLock);
                finish = _queue.DequeueAll();
            }
            finish.Dispose();
        }

        /// <summary>
        /// Asynchronously waits for a signal on this condition variable. The associated lock MUST be held when calling this method, and it will still be held when this method returns, even if the method is cancelled.
        /// </summary>
        /// <param name="cancellationToken">The cancellation signal used to cancel this wait.</param>
        public Task WaitAsync(CancellationToken cancellationToken)
        {
            lock (_mutex)
            {
                // Begin waiting for either a signal or cancellation.
                var task = _queue.Enqueue(_mutex, cancellationToken);

                // Attach to the signal or cancellation.
                var retTcs = new TaskCompletionSource();
                task.ContinueWith(async t =>
                {
                    // Re-take the lock.
                    await _asyncLock.LockAsync().ConfigureAwait(false);

                    // Propagate the cancellation exception if necessary.
                    retTcs.TryCompleteFromCompletedTask(t);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                var ret = retTcs.Task;
                //Enlightenment.Trace.AsyncConditionVariable_TrackWait(this, _asyncLock, task, ret);

                // Release the lock while we are waiting.
                _asyncLock.ReleaseLock();

                return ret;
            }
        }

        /// <summary>
        /// Asynchronously waits for a signal on this condition variable. The associated lock MUST be held when calling this method, and it will still be held when this method returns.
        /// </summary>
        public Task WaitAsync()
        {
            return WaitAsync(CancellationToken.None);
        }

        // ReSharper disable UnusedMember.Local
        [DebuggerNonUserCode]
        private sealed class DebugView
        {
            private readonly AsyncConditionVariable _cv;

            public DebugView(AsyncConditionVariable cv)
            {
                _cv = cv;
            }

            public int Id { get { return _cv.Id; } }

            public AsyncLock AsyncLock { get { return _cv._asyncLock; } }

            public IAsyncWaitQueue<object> WaitQueue { get { return _cv._queue; } }
        }
        // ReSharper restore UnusedMember.Local
    }

    /// <summary>
    /// Provides extension methods for <see cref="TaskCompletionSource{TResult}"/>.
    /// </summary>
    internal static class TaskCompletionSourceExtensions
    {
        /// <summary>
        /// Attempts to complete a <see cref="TaskCompletionSource"/>, propagating the completion of <paramref name="task"/>.
        /// </summary>
        /// <param name="this">The task completion source. May not be <c>null</c>.</param>
        /// <param name="task">The task. May not be <c>null</c>.</param>
        /// <returns><c>true</c> if this method completed the task completion source; <c>false</c> if it was already completed.</returns>
        public static bool TryCompleteFromCompletedTask(this TaskCompletionSource @this, Task task)
        {
            if (task.IsFaulted) return @this.TrySetException(task.Exception.InnerExceptions);
            if (task.IsCanceled) return @this.TrySetCanceled();
            return @this.TrySetResult();
        }
    }

    /// <summary>
        /// Represents the producer side of a <see cref="System.Threading.Tasks.Task"/> unbound to a delegate, providing access to the consumer side through the <see cref="Task"/> property.
        /// </summary>
        public sealed class TaskCompletionSource
    {
        /// <summary>
        /// The underlying TCS.
        /// </summary>
        private readonly TaskCompletionSource<object> _tcs;

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskCompletionSource"/> class.
        /// </summary>
        public TaskCompletionSource()
        {
            _tcs = new TaskCompletionSource<object>();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskCompletionSource"/> class with the specified state.
        /// </summary>
        /// <param name="state">The state to use as the underlying <see cref="Task"/>'s <see cref="IAsyncResult.AsyncState"/>.</param>
        public TaskCompletionSource(object state)
        {
            _tcs = new TaskCompletionSource<object>(state);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskCompletionSource"/> class with the specified options.
        /// </summary>
        /// <param name="creationOptions">The options to use when creating the underlying <see cref="Task"/>.</param>
        public TaskCompletionSource(TaskCreationOptions creationOptions)
        {
            _tcs = new TaskCompletionSource<object>(creationOptions);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskCompletionSource"/> class with the specified state and options.
        /// </summary>
        /// <param name="state">The state to use as the underlying <see cref="Task"/>'s <see cref="IAsyncResult.AsyncState"/>.</param>
        /// <param name="creationOptions">The options to use when creating the underlying <see cref="Task"/>.</param>
        public TaskCompletionSource(object state, TaskCreationOptions creationOptions)
        {
            _tcs = new TaskCompletionSource<object>(state, creationOptions);
        }

        /// <summary>
        /// Gets the <see cref="Task"/> created by this <see cref="TaskCompletionSource"/>.
        /// </summary>
        public Task Task
        {
            get { return _tcs.Task; }
        }

        /// <summary>
        /// Transitions the underlying <see cref="Task"/> into the <see cref="TaskStatus.Canceled"/> state.
        /// </summary>
        /// <exception cref="InvalidOperationException">The underlying <see cref="Task"/> has already been completed.</exception>
        public void SetCanceled()
        {
            _tcs.SetCanceled();
        }

        /// <summary>
        /// Attempts to transition the underlying <see cref="Task"/> into the <see cref="TaskStatus.Canceled"/> state.
        /// </summary>
        /// <returns><c>true</c> if the operation was successful; otherwise, <c>false</c>.</returns>
        public bool TrySetCanceled()
        {
            return _tcs.TrySetCanceled();
        }

        /// <summary>
        /// Transitions the underlying <see cref="Task"/> into the <see cref="TaskStatus.Faulted"/> state.
        /// </summary>
        /// <param name="exception">The exception to bind to this <see cref="Task"/>. May not be <c>null</c>.</param>
        /// <exception cref="InvalidOperationException">The underlying <see cref="Task"/> has already been completed.</exception>
        public void SetException(Exception exception)
        {
            _tcs.SetException(exception);
        }

        /// <summary>
        /// Transitions the underlying <see cref="Task"/> into the <see cref="TaskStatus.Faulted"/> state.
        /// </summary>
        /// <param name="exceptions">The collection of exceptions to bind to this <see cref="Task"/>. May not be <c>null</c> or contain <c>null</c> elements.</param>
        /// <exception cref="InvalidOperationException">The underlying <see cref="Task"/> has already been completed.</exception>
        public void SetException(IEnumerable<Exception> exceptions)
        {
            _tcs.SetException(exceptions);
        }

        /// <summary>
        /// Attempts to transition the underlying <see cref="Task"/> into the <see cref="TaskStatus.Faulted"/> state.
        /// </summary>
        /// <param name="exception">The exception to bind to this <see cref="Task"/>. May not be <c>null</c>.</param>
        /// <returns><c>true</c> if the operation was successful; otherwise, <c>false</c>.</returns>
        public bool TrySetException(Exception exception)
        {
            return _tcs.TrySetException(exception);
        }

        /// <summary>
        /// Attempts to transition the underlying <see cref="Task"/> into the <see cref="TaskStatus.Faulted"/> state.
        /// </summary>
        /// <param name="exceptions">The collection of exceptions to bind to this <see cref="Task"/>. May not be <c>null</c> or contain <c>null</c> elements.</param>
        /// <returns><c>true</c> if the operation was successful; otherwise, <c>false</c>.</returns>
        public bool TrySetException(IEnumerable<Exception> exceptions)
        {
            return _tcs.TrySetException(exceptions);
        }

        /// <summary>
        /// Transitions the underlying <see cref="Task"/> into the <see cref="TaskStatus.RanToCompletion"/> state.
        /// </summary>
        /// <exception cref="InvalidOperationException">The underlying <see cref="Task"/> has already been completed.</exception>
        public void SetResult()
        {
            _tcs.SetResult(null);
        }

        /// <summary>
        /// Attempts to transition the underlying <see cref="Task"/> into the <see cref="TaskStatus.RanToCompletion"/> state.
        /// </summary>
        /// <returns><c>true</c> if the operation was successful; otherwise, <c>false</c>.</returns>
        public bool TrySetResult()
        {
            return _tcs.TrySetResult(null);
        }
    }

    /// <summary>
    /// An async-compatible monitor.
    /// </summary>
    [DebuggerDisplay("Id = {Id}, ConditionVariableId = {_conditionVariable.Id}")]
    internal sealed class AsyncMonitor
    {
        /// <summary>
        /// The lock.
        /// </summary>
        private readonly AsyncLock _asyncLock;

        /// <summary>
        /// The condition variable.
        /// </summary>
        private readonly AsyncConditionVariable _conditionVariable;

        /// <summary>
        /// Constructs a new monitor.
        /// </summary>
        public AsyncMonitor(IAsyncWaitQueue<IDisposable> lockQueue, IAsyncWaitQueue<object> conditionVariableQueue)
        {
            _asyncLock = new AsyncLock(lockQueue);
            _conditionVariable = new AsyncConditionVariable(_asyncLock, conditionVariableQueue);
            //Enlightenment.Trace.AsyncMonitor_Created(_asyncLock, _conditionVariable);
        }

        /// <summary>
        /// Constructs a new monitor.
        /// </summary>
        public AsyncMonitor()
            : this(new DefaultAsyncWaitQueue<IDisposable>(), new DefaultAsyncWaitQueue<object>())
        {
        }

        /// <summary>
        /// Gets a semi-unique identifier for this monitor.
        /// </summary>
        public int Id
        {
            get { return _asyncLock.Id; }
        }

        /// <summary>
        /// Asynchronously enters the monitor. Returns a disposable that leaves the monitor when disposed.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token used to cancel the enter. If this is already set, then this method will attempt to enter the monitor immediately (succeeding if the monitor is currently available).</param>
        /// <returns>A disposable that leaves the monitor when disposed.</returns>
        public AwaitableDisposable<IDisposable> EnterAsync(CancellationToken cancellationToken)
        {
            return _asyncLock.LockAsync(cancellationToken);
        }

        /// <summary>
        /// Asynchronously enters the monitor. Returns a disposable that leaves the monitor when disposed.
        /// </summary>
        /// <returns>A disposable that leaves the monitor when disposed.</returns>
        public AwaitableDisposable<IDisposable> EnterAsync()
        {
            return EnterAsync(CancellationToken.None);
        }

        /// <summary>
        /// Asynchronously waits for a pulse signal on this monitor. The monitor MUST already be entered when calling this method, and it will still be entered when this method returns, even if the method is cancelled. This method internally will leave the monitor while waiting for a notification.
        /// </summary>
        /// <param name="cancellationToken">The cancellation signal used to cancel this wait.</param>
        public Task WaitAsync(CancellationToken cancellationToken)
        {
            return _conditionVariable.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Asynchronously waits for a pulse signal on this monitor. The monitor MUST already be entered when calling this method, and it will still be entered when this method returns. This method internally will leave the monitor while waiting for a notification.
        /// </summary>
        public Task WaitAsync()
        {
            return WaitAsync(CancellationToken.None);
        }

        /// <summary>
        /// Sends a signal to a single task waiting on this monitor. The monitor MUST already be entered when calling this method, and it will still be entered when this method returns.
        /// </summary>
        public void Pulse()
        {
            _conditionVariable.Notify();
        }

        /// <summary>
        /// Sends a signal to all tasks waiting on this monitor. The monitor MUST already be entered when calling this method, and it will still be entered when this method returns.
        /// </summary>
        public void PulseAll()
        {
            _conditionVariable.NotifyAll();
        }
    }

    /// <summary>
    /// A double-ended queue (deque), which provides O(1) indexed access, O(1) removals from the front and back, amortized O(1) insertions to the front and back, and O(N) insertions and removals anywhere else (with the operations getting slower as the index approaches the middle).
    /// </summary>
    /// <typeparam name="T">The type of elements contained in the deque.</typeparam>
    [DebuggerDisplay("Count = {Count}, Capacity = {Capacity}")]
    [DebuggerTypeProxy(typeof(Deque<>.DebugView))]
    internal sealed class Deque<T> : IList<T>, System.Collections.IList
    {
        /// <summary>
        /// The default capacity.
        /// </summary>
        private const int DefaultCapacity = 8;

        /// <summary>
        /// The circular buffer that holds the view.
        /// </summary>
        private T[] buffer;

        /// <summary>
        /// The offset into <see cref="buffer"/> where the view begins.
        /// </summary>
        private int offset;

        /// <summary>
        /// Initializes a new instance of the <see cref="Deque&lt;T&gt;"/> class with the specified capacity.
        /// </summary>
        /// <param name="capacity">The initial capacity. Must be greater than <c>0</c>.</param>
        public Deque(int capacity)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException("capacity", "Capacity must be greater than 0.");
            buffer = new T[capacity];
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Deque&lt;T&gt;"/> class with the elements from the specified collection.
        /// </summary>
        /// <param name="collection">The collection.</param>
        public Deque(IEnumerable<T> collection)
        {
            int count = collection.Count();
            if (count > 0)
            {
                buffer = new T[count];
                DoInsertRange(0, collection, count);
            }
            else
            {
                buffer = new T[DefaultCapacity];
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Deque&lt;T&gt;"/> class.
        /// </summary>
        public Deque()
            : this(DefaultCapacity)
        {
        }

        #region GenericListImplementations

        /// <summary>
        /// Gets a value indicating whether this list is read-only. This implementation always returns <c>false</c>.
        /// </summary>
        /// <returns>true if this list is read-only; otherwise, false.</returns>
        bool ICollection<T>.IsReadOnly
        {
            get { return false; }
        }

        /// <summary>
        /// Gets or sets the item at the specified index.
        /// </summary>
        /// <param name="index">The index of the item to get or set.</param>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="index"/> is not a valid index in this list.</exception>
        /// <exception cref="T:System.NotSupportedException">This property is set and the list is read-only.</exception>
        public T this[int index]
        {
            get
            {
                CheckExistingIndexArgument(this.Count, index);
                return DoGetItem(index);
            }

            set
            {
                CheckExistingIndexArgument(this.Count, index);
                DoSetItem(index, value);
            }
        }

        /// <summary>
        /// Inserts an item to this list at the specified index.
        /// </summary>
        /// <param name="index">The zero-based index at which <paramref name="item"/> should be inserted.</param>
        /// <param name="item">The object to insert into this list.</param>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="index"/> is not a valid index in this list.
        /// </exception>
        /// <exception cref="T:System.NotSupportedException">
        /// This list is read-only.
        /// </exception>
        public void Insert(int index, T item)
        {
            CheckNewIndexArgument(Count, index);
            DoInsert(index, item);
        }

        /// <summary>
        /// Removes the item at the specified index.
        /// </summary>
        /// <param name="index">The zero-based index of the item to remove.</param>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="index"/> is not a valid index in this list.
        /// </exception>
        /// <exception cref="T:System.NotSupportedException">
        /// This list is read-only.
        /// </exception>
        public void RemoveAt(int index)
        {
            CheckExistingIndexArgument(Count, index);
            DoRemoveAt(index);
        }

        /// <summary>
        /// Determines the index of a specific item in this list.
        /// </summary>
        /// <param name="item">The object to locate in this list.</param>
        /// <returns>The index of <paramref name="item"/> if found in this list; otherwise, -1.</returns>
        public int IndexOf(T item)
        {
            var comparer = EqualityComparer<T>.Default;
            int ret = 0;
            foreach (var sourceItem in this)
            {
                if (comparer.Equals(item, sourceItem))
                    return ret;
                ++ret;
            }

            return -1;
        }

        /// <summary>
        /// Adds an item to the end of this list.
        /// </summary>
        /// <param name="item">The object to add to this list.</param>
        /// <exception cref="T:System.NotSupportedException">
        /// This list is read-only.
        /// </exception>
        void ICollection<T>.Add(T item)
        {
            DoInsert(Count, item);
        }

        /// <summary>
        /// Determines whether this list contains a specific value.
        /// </summary>
        /// <param name="item">The object to locate in this list.</param>
        /// <returns>
        /// true if <paramref name="item"/> is found in this list; otherwise, false.
        /// </returns>
        bool ICollection<T>.Contains(T item)
        {
            return this.Contains(item, null);
        }

        /// <summary>
        /// Copies the elements of this list to an <see cref="T:System.Array"/>, starting at a particular <see cref="T:System.Array"/> index.
        /// </summary>
        /// <param name="array">The one-dimensional <see cref="T:System.Array"/> that is the destination of the elements copied from this slice. The <see cref="T:System.Array"/> must have zero-based indexing.</param>
        /// <param name="arrayIndex">The zero-based index in <paramref name="array"/> at which copying begins.</param>
        /// <exception cref="T:System.ArgumentNullException">
        /// <paramref name="array"/> is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="arrayIndex"/> is less than 0.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// <paramref name="arrayIndex"/> is equal to or greater than the length of <paramref name="array"/>.
        /// -or-
        /// The number of elements in the source <see cref="T:System.Collections.Generic.ICollection`1"/> is greater than the available space from <paramref name="arrayIndex"/> to the end of the destination <paramref name="array"/>.
        /// </exception>
        void ICollection<T>.CopyTo(T[] array, int arrayIndex)
        {
            if (array == null)
                throw new ArgumentNullException("array", "Array is null");

            int count = this.Count;
            CheckRangeArguments(array.Length, arrayIndex, count);
            for (int i = 0; i != count; ++i)
            {
                array[arrayIndex + i] = this[i];
            }
        }

        /// <summary>
        /// Removes the first occurrence of a specific object from this list.
        /// </summary>
        /// <param name="item">The object to remove from this list.</param>
        /// <returns>
        /// true if <paramref name="item"/> was successfully removed from this list; otherwise, false. This method also returns false if <paramref name="item"/> is not found in this list.
        /// </returns>
        /// <exception cref="T:System.NotSupportedException">
        /// This list is read-only.
        /// </exception>
        public bool Remove(T item)
        {
            int index = IndexOf(item);
            if (index == -1)
                return false;

            DoRemoveAt(index);
            return true;
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Collections.Generic.IEnumerator`1"/> that can be used to iterate through the collection.
        /// </returns>
        public IEnumerator<T> GetEnumerator()
        {
            int count = this.Count;
            for (int i = 0; i != count; ++i)
            {
                yield return DoGetItem(i);
            }
        }

        /// <summary>
        /// Returns an enumerator that iterates through a collection.
        /// </summary>
        /// <returns>
        /// An <see cref="T:System.Collections.IEnumerator"/> object that can be used to iterate through the collection.
        /// </returns>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        #endregion
        #region ObjectListImplementations

        int System.Collections.IList.Add(object value)
        {
            AddToBack((T)value);
            return Count - 1;
        }

        bool System.Collections.IList.Contains(object value)
        {
            return this.Contains((T)value);
        }

        int System.Collections.IList.IndexOf(object value)
        {
            return IndexOf((T)value);
        }

        void System.Collections.IList.Insert(int index, object value)
        {
            Insert(index, (T)value);
        }

        bool System.Collections.IList.IsFixedSize
        {
            get { return false; }
        }

        bool System.Collections.IList.IsReadOnly
        {
            get { return false; }
        }

        void System.Collections.IList.Remove(object value)
        {
            Remove((T)value);
        }

        object System.Collections.IList.this[int index]
        {
            get
            {
                return this[index];
            }

            set
            {
                this[index] = (T)value;
            }
        }

        void System.Collections.ICollection.CopyTo(Array array, int index)
        {
            if (array == null)
                throw new ArgumentNullException("array", "Destination array cannot be null.");
            CheckRangeArguments(array.Length, index, Count);

            for (int i = 0; i != Count; ++i)
            {
                try
                {
                    array.SetValue(this[i], index + i);
                }
                catch (InvalidCastException ex)
                {
                    throw new ArgumentException("Destination array is of incorrect type.", ex);
                }
            }
        }

        bool System.Collections.ICollection.IsSynchronized
        {
            get { return false; }
        }

        object System.Collections.ICollection.SyncRoot
        {
            get { return this; }
        }

        #endregion
        #region GenericListHelpers

        /// <summary>
        /// Checks the <paramref name="index"/> argument to see if it refers to a valid insertion point in a source of a given length.
        /// </summary>
        /// <param name="sourceLength">The length of the source. This parameter is not checked for validity.</param>
        /// <param name="index">The index into the source.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a valid index to an insertion point for the source.</exception>
        private static void CheckNewIndexArgument(int sourceLength, int index)
        {
            if (index < 0 || index > sourceLength)
            {
                throw new ArgumentOutOfRangeException("index", "Invalid new index " + index + " for source length " + sourceLength);
            }
        }

        /// <summary>
        /// Checks the <paramref name="index"/> argument to see if it refers to an existing element in a source of a given length.
        /// </summary>
        /// <param name="sourceLength">The length of the source. This parameter is not checked for validity.</param>
        /// <param name="index">The index into the source.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a valid index to an existing element for the source.</exception>
        private static void CheckExistingIndexArgument(int sourceLength, int index)
        {
            if (index < 0 || index >= sourceLength)
            {
                throw new ArgumentOutOfRangeException("index", "Invalid existing index " + index + " for source length " + sourceLength);
            }
        }

        /// <summary>
        /// Checks the <paramref name="offset"/> and <paramref name="count"/> arguments for validity when applied to a source of a given length. Allows 0-element ranges, including a 0-element range at the end of the source.
        /// </summary>
        /// <param name="sourceLength">The length of the source. This parameter is not checked for validity.</param>
        /// <param name="offset">The index into source at which the range begins.</param>
        /// <param name="count">The number of elements in the range.</param>
        /// <exception cref="ArgumentOutOfRangeException">Either <paramref name="offset"/> or <paramref name="count"/> is less than 0.</exception>
        /// <exception cref="ArgumentException">The range [offset, offset + count) is not within the range [0, sourceLength).</exception>
        private static void CheckRangeArguments(int sourceLength, int offset, int count)
        {
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException("offset", "Invalid offset " + offset);
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count", "Invalid count " + count);
            }

            if (sourceLength - offset < count)
            {
                throw new ArgumentException("Invalid offset (" + offset + ") or count + (" + count + ") for source length " + sourceLength);
            }
        }

        #endregion

        /// <summary>
        /// Gets a value indicating whether this instance is empty.
        /// </summary>
        private bool IsEmpty
        {
            get { return Count == 0; }
        }

        /// <summary>
        /// Gets a value indicating whether this instance is at full capacity.
        /// </summary>
        private bool IsFull
        {
            get { return Count == Capacity; }
        }

        /// <summary>
        /// Gets a value indicating whether the buffer is "split" (meaning the beginning of the view is at a later index in <see cref="buffer"/> than the end).
        /// </summary>
        private bool IsSplit
        {
            get
            {
                // Overflow-safe version of "(offset + Count) > Capacity"
                return offset > (Capacity - Count);
            }
        }

        /// <summary>
        /// Gets or sets the capacity for this deque. This value must always be greater than zero, and this property cannot be set to a value less than <see cref="Count"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException"><c>Capacity</c> cannot be set to a value less than <see cref="Count"/>.</exception>
        public int Capacity
        {
            get
            {
                return buffer.Length;
            }

            set
            {
                if (value < 1)
                    throw new ArgumentOutOfRangeException("value", "Capacity must be greater than 0.");

                if (value < Count)
                    throw new InvalidOperationException("Capacity cannot be set to a value less than Count");

                if (value == buffer.Length)
                    return;

                // Create the new buffer and copy our existing range.
                T[] newBuffer = new T[value];
                if (IsSplit)
                {
                    // The existing buffer is split, so we have to copy it in parts
                    int length = Capacity - offset;
                    Array.Copy(buffer, offset, newBuffer, 0, length);
                    Array.Copy(buffer, 0, newBuffer, length, Count - length);
                }
                else
                {
                    // The existing buffer is whole
                    Array.Copy(buffer, offset, newBuffer, 0, Count);
                }

                // Set up to use the new buffer.
                buffer = newBuffer;
                offset = 0;
            }
        }

        /// <summary>
        /// Gets the number of elements contained in this deque.
        /// </summary>
        /// <returns>The number of elements contained in this deque.</returns>
        public int Count { get; private set; }

        /// <summary>
        /// Applies the offset to <paramref name="index"/>, resulting in a buffer index.
        /// </summary>
        /// <param name="index">The deque index.</param>
        /// <returns>The buffer index.</returns>
        private int DequeIndexToBufferIndex(int index)
        {
            return (index + offset) % Capacity;
        }

        /// <summary>
        /// Gets an element at the specified view index.
        /// </summary>
        /// <param name="index">The zero-based view index of the element to get. This index is guaranteed to be valid.</param>
        /// <returns>The element at the specified index.</returns>
        private T DoGetItem(int index)
        {
            return buffer[DequeIndexToBufferIndex(index)];
        }

        /// <summary>
        /// Sets an element at the specified view index.
        /// </summary>
        /// <param name="index">The zero-based view index of the element to get. This index is guaranteed to be valid.</param>
        /// <param name="item">The element to store in the list.</param>
        private void DoSetItem(int index, T item)
        {
            buffer[DequeIndexToBufferIndex(index)] = item;
        }

        /// <summary>
        /// Inserts an element at the specified view index.
        /// </summary>
        /// <param name="index">The zero-based view index at which the element should be inserted. This index is guaranteed to be valid.</param>
        /// <param name="item">The element to store in the list.</param>
        private void DoInsert(int index, T item)
        {
            EnsureCapacityForOneElement();

            if (index == 0)
            {
                DoAddToFront(item);
                return;
            }
            else if (index == Count)
            {
                DoAddToBack(item);
                return;
            }

            DoInsertRange(index, new[] { item }, 1);
        }

        /// <summary>
        /// Removes an element at the specified view index.
        /// </summary>
        /// <param name="index">The zero-based view index of the element to remove. This index is guaranteed to be valid.</param>
        private void DoRemoveAt(int index)
        {
            if (index == 0)
            {
                DoRemoveFromFront();
                return;
            }
            else if (index == Count - 1)
            {
                DoRemoveFromBack();
                return;
            }

            DoRemoveRange(index, 1);
        }

        /// <summary>
        /// Increments <see cref="offset"/> by <paramref name="value"/> using modulo-<see cref="Capacity"/> arithmetic.
        /// </summary>
        /// <param name="value">The value by which to increase <see cref="offset"/>. May not be negative.</param>
        /// <returns>The value of <see cref="offset"/> after it was incremented.</returns>
        private int PostIncrement(int value)
        {
            int ret = offset;
            offset += value;
            offset %= Capacity;
            return ret;
        }

        /// <summary>
        /// Decrements <see cref="offset"/> by <paramref name="value"/> using modulo-<see cref="Capacity"/> arithmetic.
        /// </summary>
        /// <param name="value">The value by which to reduce <see cref="offset"/>. May not be negative or greater than <see cref="Capacity"/>.</param>
        /// <returns>The value of <see cref="offset"/> before it was decremented.</returns>
        private int PreDecrement(int value)
        {
            offset -= value;
            if (offset < 0)
                offset += Capacity;
            return offset;
        }

        /// <summary>
        /// Inserts a single element to the back of the view. <see cref="IsFull"/> must be false when this method is called.
        /// </summary>
        /// <param name="value">The element to insert.</param>
        private void DoAddToBack(T value)
        {
            buffer[DequeIndexToBufferIndex(Count)] = value;
            ++Count;
        }

        /// <summary>
        /// Inserts a single element to the front of the view. <see cref="IsFull"/> must be false when this method is called.
        /// </summary>
        /// <param name="value">The element to insert.</param>
        private void DoAddToFront(T value)
        {
            buffer[PreDecrement(1)] = value;
            ++Count;
        }

        /// <summary>
        /// Removes and returns the last element in the view. <see cref="IsEmpty"/> must be false when this method is called.
        /// </summary>
        /// <returns>The former last element.</returns>
        private T DoRemoveFromBack()
        {
            T ret = buffer[DequeIndexToBufferIndex(Count - 1)];
            --Count;
            return ret;
        }

        /// <summary>
        /// Removes and returns the first element in the view. <see cref="IsEmpty"/> must be false when this method is called.
        /// </summary>
        /// <returns>The former first element.</returns>
        private T DoRemoveFromFront()
        {
            --Count;
            return buffer[PostIncrement(1)];
        }

        /// <summary>
        /// Inserts a range of elements into the view.
        /// </summary>
        /// <param name="index">The index into the view at which the elements are to be inserted.</param>
        /// <param name="collection">The elements to insert.</param>
        /// <param name="collectionCount">The number of elements in <paramref name="collection"/>. Must be greater than zero, and the sum of <paramref name="collectionCount"/> and <see cref="Count"/> must be less than or equal to <see cref="Capacity"/>.</param>
        private void DoInsertRange(int index, IEnumerable<T> collection, int collectionCount)
        {
            // Make room in the existing list
            if (index < Count / 2)
            {
                // Inserting into the first half of the list

                // Move lower items down: [0, index) -> [Capacity - collectionCount, Capacity - collectionCount + index)
                // This clears out the low "index" number of items, moving them "collectionCount" places down;
                //   after rotation, there will be a "collectionCount"-sized hole at "index".
                int copyCount = index;
                int writeIndex = Capacity - collectionCount;
                for (int j = 0; j != copyCount; ++j)
                    buffer[DequeIndexToBufferIndex(writeIndex + j)] = buffer[DequeIndexToBufferIndex(j)];

                // Rotate to the new view
                this.PreDecrement(collectionCount);
            }
            else
            {
                // Inserting into the second half of the list

                // Move higher items up: [index, count) -> [index + collectionCount, collectionCount + count)
                int copyCount = Count - index;
                int writeIndex = index + collectionCount;
                for (int j = copyCount - 1; j != -1; --j)
                    buffer[DequeIndexToBufferIndex(writeIndex + j)] = buffer[DequeIndexToBufferIndex(index + j)];
            }

            // Copy new items into place
            int i = index;
            foreach (T item in collection)
            {
                buffer[DequeIndexToBufferIndex(i)] = item;
                ++i;
            }

            // Adjust valid count
            Count += collectionCount;
        }

        /// <summary>
        /// Removes a range of elements from the view.
        /// </summary>
        /// <param name="index">The index into the view at which the range begins.</param>
        /// <param name="collectionCount">The number of elements in the range. This must be greater than 0 and less than or equal to <see cref="Count"/>.</param>
        private void DoRemoveRange(int index, int collectionCount)
        {
            if (index == 0)
            {
                // Removing from the beginning: rotate to the new view
                this.PostIncrement(collectionCount);
                Count -= collectionCount;
                return;
            }
            else if (index == Count - collectionCount)
            {
                // Removing from the ending: trim the existing view
                Count -= collectionCount;
                return;
            }

            if ((index + (collectionCount / 2)) < Count / 2)
            {
                // Removing from first half of list

                // Move lower items up: [0, index) -> [collectionCount, collectionCount + index)
                int copyCount = index;
                int writeIndex = collectionCount;
                for (int j = copyCount - 1; j != -1; --j)
                    buffer[DequeIndexToBufferIndex(writeIndex + j)] = buffer[DequeIndexToBufferIndex(j)];

                // Rotate to new view
                this.PostIncrement(collectionCount);
            }
            else
            {
                // Removing from second half of list

                // Move higher items down: [index + collectionCount, count) -> [index, count - collectionCount)
                int copyCount = Count - collectionCount - index;
                int readIndex = index + collectionCount;
                for (int j = 0; j != copyCount; ++j)
                    buffer[DequeIndexToBufferIndex(index + j)] = buffer[DequeIndexToBufferIndex(readIndex + j)];
            }

            // Adjust valid count
            Count -= collectionCount;
        }

        /// <summary>
        /// Doubles the capacity if necessary to make room for one more element. When this method returns, <see cref="IsFull"/> is false.
        /// </summary>
        private void EnsureCapacityForOneElement()
        {
            if (this.IsFull)
            {
                this.Capacity = this.Capacity * 2;
            }
        }

        /// <summary>
        /// Inserts a single element at the back of this deque.
        /// </summary>
        /// <param name="value">The element to insert.</param>
        public void AddToBack(T value)
        {
            EnsureCapacityForOneElement();
            DoAddToBack(value);
        }

        /// <summary>
        /// Inserts a single element at the front of this deque.
        /// </summary>
        /// <param name="value">The element to insert.</param>
        public void AddToFront(T value)
        {
            EnsureCapacityForOneElement();
            DoAddToFront(value);
        }

        /// <summary>
        /// Inserts a collection of elements into this deque.
        /// </summary>
        /// <param name="index">The index at which the collection is inserted.</param>
        /// <param name="collection">The collection of elements to insert.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a valid index to an insertion point for the source.</exception>
        public void InsertRange(int index, IEnumerable<T> collection)
        {
            int collectionCount = collection.Count();
            CheckNewIndexArgument(Count, index);

            // Overflow-safe check for "this.Count + collectionCount > this.Capacity"
            if (collectionCount > Capacity - Count)
            {
                this.Capacity = checked(Count + collectionCount);
            }

            if (collectionCount == 0)
            {
                return;
            }

            this.DoInsertRange(index, collection, collectionCount);
        }

        /// <summary>
        /// Removes a range of elements from this deque.
        /// </summary>
        /// <param name="offset">The index into the deque at which the range begins.</param>
        /// <param name="count">The number of elements to remove.</param>
        /// <exception cref="ArgumentOutOfRangeException">Either <paramref name="offset"/> or <paramref name="count"/> is less than 0.</exception>
        /// <exception cref="ArgumentException">The range [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="count"/>) is not within the range [0, <see cref="Count"/>).</exception>
        public void RemoveRange(int offset, int count)
        {
            CheckRangeArguments(Count, offset, count);

            if (count == 0)
            {
                return;
            }

            this.DoRemoveRange(offset, count);
        }

        /// <summary>
        /// Removes and returns the last element of this deque.
        /// </summary>
        /// <returns>The former last element.</returns>
        /// <exception cref="InvalidOperationException">The deque is empty.</exception>
        public T RemoveFromBack()
        {
            if (this.IsEmpty)
                throw new InvalidOperationException("The deque is empty.");

            return this.DoRemoveFromBack();
        }

        /// <summary>
        /// Removes and returns the first element of this deque.
        /// </summary>
        /// <returns>The former first element.</returns>
        /// <exception cref="InvalidOperationException">The deque is empty.</exception>
        public T RemoveFromFront()
        {
            if (this.IsEmpty)
                throw new InvalidOperationException("The deque is empty.");

            return this.DoRemoveFromFront();
        }

        /// <summary>
        /// Removes all items from this deque.
        /// </summary>
        public void Clear()
        {
            this.offset = 0;
            this.Count = 0;
        }

        [DebuggerNonUserCode]
        private sealed class DebugView
        {
            private readonly Deque<T> deque;

            public DebugView(Deque<T> deque)
            {
                this.deque = deque;
            }

            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public T[] Items
            {
                get
                {
                    var array = new T[deque.Count];
                    ((ICollection<T>)deque).CopyTo(array, 0);
                    return array;
                }
            }
        }
    }
}