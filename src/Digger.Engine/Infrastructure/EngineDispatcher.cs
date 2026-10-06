using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Digger.Engine.Infrastructure;

/// <summary>
/// The engine's single logical thread. Protocol requests and debugger callbacks are both
/// posted here, so engine state is only ever touched from one thread and needs no locks.
/// </summary>
public sealed class EngineDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public EngineDispatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Digger engine" };
        _thread.Start();
    }

    public bool IsEngineThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    public void Post(Action work)
    {
        if (!_queue.IsAddingCompleted)
        {
            try
            {
                _queue.Add(work);
            }
            catch (InvalidOperationException)
            {
                // Shutting down; work posted after completion is dropped.
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> on the engine thread and blocks until it finishes.</summary>
    public void Invoke(Action work)
    {
        if (IsEngineThread)
        {
            work();
            return;
        }

        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        Post(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // A faulting work item must not take the engine down.
                Log.Error("Unhandled exception on engine thread", ex);
            }
        }
    }

    /// <summary>Stops accepting work, drains what is queued, and waits for the engine thread.</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        if (IsEngineThread)
        {
            return; // the loop ends after the current item; the collection is left to the GC
        }

        if (_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _queue.Dispose();
        }
    }
}
