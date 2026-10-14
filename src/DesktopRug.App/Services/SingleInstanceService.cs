using System.Collections.Concurrent;

namespace DesktopRug.App.Services;

/// <summary>
/// Per-session named mutex and show signal. Construct and dispose a primary instance on the same
/// owning thread; callbacks are posted through the supplied dispatcher and never activate a window.
/// </summary>
internal sealed class SingleInstanceService : IDisposable
{
    private static readonly object OwnershipLock = new();
    private static readonly HashSet<string> OwnedNames = [];
    private readonly string _prefix;
    private readonly Action<Action> _postToOwner;
    private readonly Action _showRequested;
    private readonly int _ownerThread;
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _registration;
    private volatile bool _disposed;

    internal bool IsPrimary { get; private set; }

    internal SingleInstanceService(Action<Action> postToOwner, Action showRequested, string prefix = @"Local\DesktopRug")
    {
        ArgumentNullException.ThrowIfNull(postToOwner);
        ArgumentNullException.ThrowIfNull(showRequested);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        _prefix = prefix; _postToOwner = postToOwner; _showRequested = showRequested;
        _ownerThread = Environment.CurrentManagedThreadId;
        try
        {
            // Create the signal before claiming ownership. A second launch can signal while the
            // primary is still loading settings; AutoReset retains that signal until registration.
            _showSignal = new(false, EventResetMode.AutoReset, prefix + ".Show");
            _mutex = new(false, prefix + ".Instance");
            lock (OwnershipLock)
            {
                // A Windows mutex is recursive on its owning thread. This process-local guard
                // prevents a second service on that thread from being mistaken for a new owner.
                if (!OwnedNames.Contains(prefix))
                {
                    try { IsPrimary = _mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { IsPrimary = true; }
                    if (IsPrimary) OwnedNames.Add(prefix);
                }
            }
            if (IsPrimary)
                _registration = ThreadPool.RegisterWaitForSingleObject(_showSignal,
                    (_, _) => PostShowRequest(), null, Timeout.Infinite, false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool SignalExisting()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return !IsPrimary && _showSignal!.Set();
    }

    private void PostShowRequest()
    {
        if (_disposed) return;
        try
        {
            _postToOwner(() =>
            {
                if (!_disposed) _showRequested();
            });
        }
        // A pending kernel notification can race dispatcher shutdown. It must not resurrect UI
        // or escape as an unhandled ThreadPool exception after the application has exited.
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (IsPrimary && Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Dispose the primary single-instance service on its owning thread.");
        _disposed = true;
        _registration?.Unregister(null); _registration = null;
        _showSignal?.Dispose(); _showSignal = null;
        if (IsPrimary)
        {
            lock (OwnershipLock)
            {
                _mutex!.ReleaseMutex();
                OwnedNames.Remove(_prefix);
            }
        }
        _mutex?.Dispose(); _mutex = null;
    }

    /// <summary>
    /// Exercises actual named Windows kernel objects under a fresh diagnostic prefix. This never
    /// opens the live application's names, creates windows, activates UI, or changes the registry.
    /// </summary>
    internal static Task<SingleInstanceDiagnostic> DiagnoseAsync() => Task.Run(() =>
    {
        string prefix = @"Local\DesktopRug.Diagnostics." + Guid.NewGuid().ToString("N");
        int owner = Environment.CurrentManagedThreadId, received = 0, callbackThread = 0;
        var dispatch = new ConcurrentQueue<Action>();
        using var queued = new ManualResetEventSlim();
        void Post(Action action) { dispatch.Enqueue(action); queued.Set(); }
        void OnShow() { received++; callbackThread = Environment.CurrentManagedThreadId; }

        using var primary = new SingleInstanceService(Post, OnShow, prefix);
        using var secondary = new SingleInstanceService(Post, OnShow, prefix);
        bool primaryAcquired = primary.IsPrimary, secondaryRejected = !secondary.IsPrimary;
        bool kernelExclusive = false;
        Exception? probeError = null;
        var probeThread = new Thread(() =>
        {
            // Bypass the in-process recursion guard to prove Windows itself excludes another
            // thread holding a separate handle to the same named kernel mutex.
            try
            {
                using var probe = Mutex.OpenExisting(prefix + ".Instance");
                bool acquired;
                try { acquired = probe.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                kernelExclusive = !acquired;
                if (acquired) probe.ReleaseMutex();
            }
            catch (Exception exception) { probeError = exception; }
        })
        { IsBackground = true };
        probeThread.Start();
        bool probeFinished = probeThread.Join(TimeSpan.FromSeconds(3));
        bool sent = secondary.SignalExisting();
        if (queued.Wait(TimeSpan.FromSeconds(3)))
            while (dispatch.TryDequeue(out var action)) action();
        bool delivered = sent && received == 1;
        bool dispatchedToOwner = delivered && callbackThread == owner;
        queued.Reset();
        bool secondSignalQueued = secondary.SignalExisting() && queued.Wait(TimeSpan.FromSeconds(3));
        primary.Dispose();
        while (dispatch.TryDequeue(out var staleAction)) staleAction();
        bool noCallbackAfterDispose = secondSignalQueued && received == 1;
        // Keep the secondary's handles alive while reacquiring: ownership depends on the mutex
        // lease, not merely on whether another process still has an object handle open.
        using var nextPrimary = new SingleInstanceService(Post, OnShow, prefix);
        return new SingleInstanceDiagnostic(primaryAcquired, secondaryRejected, probeFinished && probeError is null && kernelExclusive,
            delivered, dispatchedToOwner, noCallbackAfterDispose, nextPrimary.IsPrimary);
    });
}

internal sealed record SingleInstanceDiagnostic(bool PrimaryAcquired, bool SecondaryRejected, bool KernelMutexExclusive,
    bool ShowSignalDelivered, bool CallbackOnOwnerThread, bool NoCallbackAfterDispose, bool ReacquiredAfterDispose)
{
    public bool Passed => PrimaryAcquired && SecondaryRejected && KernelMutexExclusive && ShowSignalDelivered
        && CallbackOnOwnerThread && NoCallbackAfterDispose && ReacquiredAfterDispose;
}
