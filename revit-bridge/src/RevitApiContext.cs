using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using Autodesk.Revit.UI;

namespace RevitMcpBridge
{
    /// <summary>
    /// The API-context pump, and the reason this add-in exists in the shape it does.
    ///
    /// Revit API calls are only legal on Revit's main thread, from inside an event Revit itself
    /// raises. HttpListener hands us requests on arbitrary thread-pool threads, where touching a
    /// Document is undefined behaviour (usually an immediate "Attempting to modify the model
    /// outside of a transaction / outside the API context" exception, sometimes a hard crash).
    ///
    /// So every request crosses the thread boundary through here:
    ///   1. the worker thread enqueues a work item and calls ExternalEvent.Raise(),
    ///   2. Revit calls Execute() on the main thread when it is idle, which drains the queue,
    ///   3. the worker thread wakes up on the item's ManualResetEventSlim and gets the result
    ///      (or the exception, rethrown with its original stack trace preserved).
    ///
    /// It is a queue rather than a single slot precisely so two concurrent requests cannot clobber
    /// each other's result.
    /// </summary>
    internal sealed class RevitApiContext : IExternalEventHandler, IDisposable
    {
        private const int StatePending = 0;
        private const int StateRunning = 1;
        private const int StateAbandoned = 2;

        private readonly ConcurrentQueue<WorkItem> _queue = new ConcurrentQueue<WorkItem>();

        private ExternalEvent _externalEvent;

        /// <summary>
        /// Must run on Revit's main thread - ExternalEvent.Create is itself an API call.
        /// </summary>
        internal void Initialize()
        {
            _externalEvent = ExternalEvent.Create(this);
        }

        /// <summary>
        /// Called from a listener thread. Blocks until the main thread has run <paramref name="work"/>
        /// or <paramref name="timeoutMs"/> elapses.
        /// </summary>
        internal object Run(Func<UIApplication, object> work, int timeoutMs)
        {
            ExternalEvent externalEvent = _externalEvent;
            if (externalEvent == null)
            {
                throw new BridgeException(
                    503,
                    "BRIDGE_NOT_READY",
                    "The bridge is not accepting work (Revit is starting up or shutting down).");
            }

            WorkItem item = new WorkItem(work);
            _queue.Enqueue(item);
            externalEvent.Raise();

            if (!item.Done.Wait(timeoutMs))
            {
                // Claim the item so Execute() skips it. If the CAS fails the main thread already
                // picked it up and is running it right now - we cannot cancel a Revit API call, so
                // we report that honestly instead of pretending nothing happened.
                int previous = Interlocked.CompareExchange(ref item.State, StateAbandoned, StatePending);
                bool startedRunning = previous != StatePending;

                BridgeLog.Warn("Work item timed out after " + timeoutMs + " ms (startedRunning="
                    + startedRunning + ")");

                // Deliberately not disposing item.Done here: when startedRunning is true the main
                // thread still holds a reference and will Set() it. Letting the GC take it is the
                // only safe option.
                throw BridgeException.Busy(timeoutMs, startedRunning);
            }

            try
            {
                if (item.Error != null)
                {
                    ExceptionDispatchInfo.Capture(item.Error).Throw();
                }

                return item.Result;
            }
            finally
            {
                // Safe: Execute() Set() the event and never touches the item again.
                item.Done.Dispose();
            }
        }

        /// <summary>Runs on Revit's main thread, inside the event Revit raised for us.</summary>
        public void Execute(UIApplication app)
        {
            WorkItem item;
            while (_queue.TryDequeue(out item))
            {
                // Skip anything whose caller already gave up - silently mutating the model after a
                // request has been answered with 503 would be the worst kind of surprise.
                if (Interlocked.CompareExchange(ref item.State, StateRunning, StatePending) != StatePending)
                {
                    BridgeLog.Warn("Skipping an abandoned work item");
                    continue;
                }

                try
                {
                    item.Result = item.Work(app);
                }
                catch (Exception ex)
                {
                    item.Error = ex;
                }
                finally
                {
                    item.Done.Set();
                }
            }

            // A request enqueued while we were draining may have raised into the very event we are
            // servicing. Re-raise so it is not stranded until some later request comes along.
            ExternalEvent externalEvent = _externalEvent;
            if (externalEvent != null && !_queue.IsEmpty)
            {
                externalEvent.Raise();
            }
        }

        public string GetName()
        {
            return "Revit MCP Bridge";
        }

        /// <summary>Must run on Revit's main thread (OnShutdown).</summary>
        public void Dispose()
        {
            ExternalEvent externalEvent = Interlocked.Exchange(ref _externalEvent, null);
            if (externalEvent != null)
            {
                externalEvent.Dispose();
            }

            // Release anyone still waiting rather than letting them sit out their full timeout.
            WorkItem item;
            while (_queue.TryDequeue(out item))
            {
                if (Interlocked.CompareExchange(ref item.State, StateRunning, StatePending) == StatePending)
                {
                    item.Error = new BridgeException(
                        503,
                        "BRIDGE_SHUTTING_DOWN",
                        "Revit is shutting down; the request was not executed.");
                    item.Done.Set();
                }
            }
        }

        private sealed class WorkItem
        {
            internal readonly Func<UIApplication, object> Work;
            internal readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);

            internal int State = StatePending;
            internal object Result;
            internal Exception Error;

            internal WorkItem(Func<UIApplication, object> work)
            {
                Work = work;
            }
        }
    }
}
