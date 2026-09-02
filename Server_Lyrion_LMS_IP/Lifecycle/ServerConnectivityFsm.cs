// ---------------------------------------------------------------------------
//  Server_Lyrion_LMS_IP - Lyrion Server driver (Driver 1 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Threading;
using LyrionCommunity.Crestron.Lyrion.Server.Transport;

namespace LyrionCommunity.Crestron.Lyrion.Server.Lifecycle
{
    /// <summary>
    /// Smooths raw <see cref="LmsConnectionState"/> transitions into the
    /// three logical states that CLAUDE.md mandates we log: DISCONNECTED,
    /// CONNECTING, CONNECTED.
    /// </summary>
    /// <remarks>
    /// Two suppression rules apply:
    /// <list type="number">
    /// <item><b>Minimum stable time</b> — a state must hold for 5 seconds
    /// before its transition is logged.</item>
    /// <item><b>Oscillation suppression</b> — when transitions arrive faster
    /// than the minimum stable interval more than once in a row, log a
    /// single "connectivity unstable" message and suppress intermediate
    /// transitions until the connection holds steady. The final stable
    /// transition is always logged.</item>
    /// </list>
    /// Consumers subscribe to <see cref="SmoothedTransition"/> to receive the
    /// committed logical state.
    /// </remarks>
    internal sealed class ServerConnectivityFsm : IDisposable
    {
        private static readonly TimeSpan MinStable = TimeSpan.FromSeconds(5);

        private readonly object _gate = new object();
        private readonly Action<string> _infoLog;

        private LogicalConnectivityState _committed = LogicalConnectivityState.Disconnected;
        private LogicalConnectivityState _pending = LogicalConnectivityState.Disconnected;
        private DateTime _pendingSinceUtc = DateTime.UtcNow;
        private DateTime _lastCommittedUtc = DateTime.UtcNow;
        private bool _oscillationLogged;
        private bool _disposed;

        private Timer _commitTimer;

        public ServerConnectivityFsm(Action<string> infoLog)
        {
            _infoLog = infoLog ?? (_ => { });
        }

        /// <summary>Raised once per committed (post-smoothing) transition.</summary>
        public event Action<LogicalConnectivityState> SmoothedTransition;

        public LogicalConnectivityState Current
        {
            get { lock (_gate) return _committed; }
        }

        public void OnRawTransition(LmsConnectionState raw)
        {
            var logical = Map(raw);
            var now = DateTime.UtcNow;

            lock (_gate)
            {
                if (_disposed) return;
                if (_pending == logical)
                {
                    ScheduleCommit_NoLock();
                    return;
                }

                var fastFlap = now - _lastCommittedUtc < MinStable;
                _pending = logical;
                _pendingSinceUtc = now;

                if (fastFlap && !_oscillationLogged)
                {
                    _oscillationLogged = true;
                    try { _infoLog("Lyrion Server: LMS connectivity unstable - suppressing transition logs"); }
                    catch { }
                }

                ScheduleCommit_NoLock();
            }
        }

        private void ScheduleCommit_NoLock()
        {
            if (_commitTimer == null)
            {
                _commitTimer = new Timer(_ => TryCommit(), null, MinStable, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _commitTimer.Change(MinStable, Timeout.InfiniteTimeSpan);
            }
        }

        private void TryCommit()
        {
            LogicalConnectivityState committed;
            bool publish = false;

            lock (_gate)
            {
                if (_disposed) return;

                // Has _pending been stable long enough?
                if (DateTime.UtcNow - _pendingSinceUtc + TimeSpan.FromMilliseconds(50) < MinStable)
                {
                    ScheduleCommit_NoLock();
                    return;
                }

                if (_committed != _pending)
                {
                    _committed = _pending;
                    _lastCommittedUtc = DateTime.UtcNow;
                    publish = true;
                    // Clear inside the same lock that commits the transition so
                    // a concurrent OnRawTransition cannot observe a stale "true"
                    // and skip a legitimate oscillation log.
                    _oscillationLogged = false;
                }
                committed = _committed;
            }

            if (!publish) return;

            try { _infoLog("Lyrion Server: LMS " + committed.ToString().ToUpperInvariant()); }
            catch { }

            try { SmoothedTransition?.Invoke(committed); }
            catch { }
        }

        private static LogicalConnectivityState Map(LmsConnectionState raw)
        {
            switch (raw)
            {
                case LmsConnectionState.Connected: return LogicalConnectivityState.Connected;
                case LmsConnectionState.Connecting: return LogicalConnectivityState.Connecting;
                default: return LogicalConnectivityState.Disconnected;
            }
        }

        public void Dispose()
        {
            Timer t;
            lock (_gate)
            {
                _disposed = true;
                t = _commitTimer;
                _commitTimer = null;
            }
            try { t?.Dispose(); } catch { }
        }
    }

    public enum LogicalConnectivityState
    {
        Disconnected,
        Connecting,
        Connected
    }
}
