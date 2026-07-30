using System;
using System.Threading;

namespace NexusApp.Services;

/// <summary>
/// Detects a second Nexus process launch and hands control back to the already-running instance
/// instead of opening a duplicate main window (issue #29: a minimized-and-forgotten Nexus, launched
/// again from the desktop shortcut, used to spawn a second window). A per-user named Mutex claims
/// "primary" for the life of the process; a named EventWaitHandle lets a second instance ask the
/// primary to restore itself before it exits.
///
/// Pure Win32-kernel-object plumbing, deliberately WPF-free: the dispatcher-dependent window-restore
/// action is a thin <see cref="Action"/> the caller injects into <see cref="StartActivationListener"/>,
/// so every decision here (acquire, signal, listen, dispose) is headlessly unit-testable.
///
/// Both names are constructor parameters (with production defaults below) rather than hardwired, for
/// two reasons: the test suite needs unique, collision-free names to run guards in parallel, and
/// production itself needs more than one "primary" domain - the Admin demo profile kit intentionally
/// launches a second NexusApp.exe (--demo-profile) alongside the live instance for screenshots
/// (<see cref="DemoProfile.StartDemoInstance"/>), and that second process must NOT be treated as a
/// duplicate live launch. The composition root (App.xaml.cs) is expected to append a profile-scoped
/// suffix to these defaults so the live and demo profiles each get their own single-instance domain.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>Production default mutex name. "Local\" scopes it to the current login session (per-user).</summary>
    public const string DefaultMutexName = "Local\\NexusApp.SingleInstance";

    /// <summary>Production default name for the "please restore yourself" signal.</summary>
    public const string DefaultActivateEventName = "Local\\NexusApp.Activate";

    private readonly string _mutexName;
    private readonly string _eventName;

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private bool _isPrimary;

    private Thread? _listenerThread;
    private volatile bool _stopListener;

    // How often the listener loop wakes up to check for a stop request. Bounded so Dispose never
    // blocks the caller for more than roughly this long waiting for the thread to join.
    private static readonly TimeSpan ListenerPollInterval = TimeSpan.FromMilliseconds(250);

    public SingleInstanceGuard(string? mutexName = null, string? eventName = null)
    {
        _mutexName = mutexName ?? DefaultMutexName;
        _eventName = eventName ?? DefaultActivateEventName;
    }

    /// <summary>
    /// Claims the named mutex for this process. Returns true when this instance is primary (the
    /// mutex was unowned, or its previous owner crashed without releasing it - an
    /// <see cref="AbandonedMutexException"/> still means WaitOne granted US ownership before it threw,
    /// so it is treated as a normal, successful acquire). This also keeps the portable self-swap
    /// relaunch path safe: the outgoing process's mutex may not have been released yet the instant the
    /// new version's process starts. Returns false when another live instance already holds it.
    /// </summary>
    public bool TryAcquirePrimary()
    {
        _mutex = new Mutex(initiallyOwned: false, _mutexName);
        try
        {
            _isPrimary = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _isPrimary = true;
        }
        return _isPrimary;
    }

    /// <summary>
    /// Called by a second-instance process (one whose <see cref="TryAcquirePrimary"/> returned false):
    /// signals the primary instance to restore itself, then returns immediately. Safe to call even if
    /// the primary's listener has not started yet - the underlying event still latches, it is only the
    /// callback timing that is not guaranteed in that narrow startup race. The caller must still exit
    /// afterward regardless of whether a primary was actually listening (this method cannot know).
    /// </summary>
    public void SignalPrimary()
    {
        using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, _eventName, out _);
        ev.Set();
    }

    /// <summary>
    /// Primary-only: starts a background wait loop that invokes <paramref name="onActivate"/> every
    /// time a second instance calls <see cref="SignalPrimary"/>. The callback runs on the listener's
    /// background thread, NOT the UI dispatcher - callers touching WPF must marshal themselves. A no-op
    /// if this guard never became primary (defensive; callers are expected to check
    /// <see cref="TryAcquirePrimary"/> first).
    /// </summary>
    public void StartActivationListener(Action onActivate)
    {
        if (!_isPrimary || _listenerThread != null) return;

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _eventName, out _);
        _listenerThread = new Thread(() =>
        {
            while (!_stopListener)
            {
                // Bounded wait: Dispose must be able to stop this loop promptly instead of the
                // thread blocking on the event forever.
                bool signaled;
                try { signaled = _activateEvent.WaitOne(ListenerPollInterval); }
                catch (ObjectDisposedException) { break; }
                if (signaled && !_stopListener) onActivate();
            }
        })
        { IsBackground = true, Name = "NexusSingleInstanceListener" };
        _listenerThread.Start();
    }

    public void Dispose()
    {
        _stopListener = true;
        _listenerThread?.Join(TimeSpan.FromSeconds(2));
        _listenerThread = null;

        if (_isPrimary)
        {
            try { _mutex?.ReleaseMutex(); }
            catch { /* already released or never actually owned - safe to ignore on teardown */ }
        }
        _mutex?.Dispose();
        _mutex = null;
        _activateEvent?.Dispose();
        _activateEvent = null;
    }
}
