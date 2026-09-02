// ---------------------------------------------------------------------------
//  Server_Lyrion_LMS_IP - Lyrion Server driver (Driver 1 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using LyrionCommunity.Crestron.Lyrion.Server.Protocol;
using LyrionCommunity.Crestron.Lyrion.Server.Registry;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Server.Services
{
    /// <summary>
    /// Implementation of the cross-driver Lyrion Server service. Commands are
    /// translated into LMS CLI lines and dispatched via the supplied sender;
    /// events are forwarded from the registry. Commands issued while the
    /// Lyrion Server is not CONNECTED are dropped (per CLAUDE.md "Commands dropped
    /// when server is not connected").
    /// </summary>
    internal sealed class LyrionServerServiceImpl : ILyrionServerService
    {
        private readonly PlayerRegistry _registry;
        private readonly Func<string, bool> _sendCliLine;
        private readonly Func<bool> _isServerConnected;
        private readonly Action<string> _onPlayerBound;

        public LyrionServerServiceImpl(
            PlayerRegistry registry,
            Func<string, bool> sendCliLine,
            Func<bool> isServerConnected,
            Action<string> onPlayerBound)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _sendCliLine = sendCliLine ?? throw new ArgumentNullException(nameof(sendCliLine));
            _isServerConnected = isServerConnected ?? throw new ArgumentNullException(nameof(isServerConnected));
            _onPlayerBound = onPlayerBound ?? (_ => { });

            // Multicast delegates short-circuit on the first throwing subscriber
            // (e.g. a buggy Source driver would prevent the Helper and Receiver
            // drivers from seeing the same event). Iterate the invocation list
            // manually so one misbehaving consumer cannot suppress the others.
            registry.AvailabilityChanged += (mac, b) => Fan2(AvailabilityChanged, mac, b);
            registry.NameChanged += (mac, s) => Fan2(NameChanged, mac, s);
            registry.PowerStateChanged += (mac, b) => Fan2(PowerStateChanged, mac, b);
            registry.PlaybackStateChanged += (mac, s) => Fan2(PlaybackStateChanged, mac, s);
            registry.MetadataUpdated += (mac, m) => Fan2(MetadataUpdated, mac, m);
            registry.ShuffleChanged += (mac, b) => Fan2(ShuffleChanged, mac, b);
            registry.RepeatChanged += (mac, b) => Fan2(RepeatChanged, mac, b);
            registry.VolumeChanged += (mac, v) => Fan2(VolumeChanged, mac, v);
            registry.VolumeStepChanged += (mac, v) => Fan2(VolumeStepChanged, mac, v);
            registry.MuteChanged += (mac, b) => Fan2(MuteChanged, mac, b);
        }

        private static void Fan2<T1, T2>(Action<T1, T2> handler, T1 a, T2 b)
        {
            if (handler == null) return;
            var list = handler.GetInvocationList();
            for (var i = 0; i < list.Length; i++)
            {
                try { ((Action<T1, T2>)list[i])(a, b); }
                catch { }
            }
        }

        // ===== Service identity =====

        public string ServiceVersion => "1.0";

        // ===== Bind / snapshot =====

        public bool BindPlayer(string mac)
        {
            if (!_registry.Bind(mac, out var created)) return false;

            // First-bind work (the initial status subscribe) runs once per
            // player, not once per consumer: the contract says repeat binds
            // are no-ops, and three consumers binding the same MAC at boot
            // used to open three subscriptions for one player.
            if (created)
            {
                try { _onPlayerBound(MacAddress.Normalize(mac)); }
                catch { }
            }
            return true;
        }

        public void UnbindPlayer(string mac) => _registry.Unbind(mac);

        public bool TryGetSnapshot(string mac, out LyrionPlayerSnapshot snapshot)
            => _registry.TryGetSnapshot(mac, out snapshot);

        // ===== Events =====

        public event Action<bool> ServerConnectivityChanged;
        public event Action<string, bool> AvailabilityChanged;
        public event Action<string, string> NameChanged;
        public event Action<string, bool> PowerStateChanged;
        public event Action<string, LyrionPlaybackState> PlaybackStateChanged;
        public event Action<string, LyrionMetadata> MetadataUpdated;
        public event Action<string, bool> ShuffleChanged;
        public event Action<string, bool> RepeatChanged;
        public event Action<string, int> VolumeChanged;
        public event Action<string, int> VolumeStepChanged;
        public event Action<string, bool> MuteChanged;

        internal void RaiseServerConnectivityChanged(bool connected)
        {
            var handler = ServerConnectivityChanged;
            if (handler == null) return;
            var list = handler.GetInvocationList();
            for (var i = 0; i < list.Length; i++)
            {
                try { ((Action<bool>)list[i])(connected); }
                catch { }
            }
        }

        // ===== Commands: Source (Driver 2) and Helper (Driver 3) =====

        public void Play(string mac) => SendForPlayer(mac, LmsCliCommands.Play);
        public void Pause(string mac) => SendForPlayer(mac, LmsCliCommands.Pause);
        public void Stop(string mac) => SendForPlayer(mac, LmsCliCommands.Stop);
        public void Next(string mac) => SendForPlayer(mac, LmsCliCommands.NextTrack);
        public void Previous(string mac) => SendForPlayer(mac, LmsCliCommands.PreviousTrack);

        public void Seek(string mac, int positionSeconds)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null || !_registry.IsBound(canon) || !_isServerConnected()) return;
            _sendCliLine(LmsCliCommands.SeekTo(canon, positionSeconds));
        }

        public void SetShuffle(string mac, bool enabled)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null || !_registry.IsBound(canon) || !_isServerConnected()) return;
            _sendCliLine(LmsCliCommands.SetShuffle(canon, enabled));
        }

        public void SetRepeat(string mac, bool enabled)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null || !_registry.IsBound(canon) || !_isServerConnected()) return;
            _sendCliLine(LmsCliCommands.SetRepeat(canon, enabled));
        }

        public void PowerOn(string mac)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null || !_registry.IsBound(canon) || !_isServerConnected()) return;

            _registry.TryGetCapabilities(canon, out var canPowerOff, out _);
            if (canPowerOff)
            {
                _sendCliLine(LmsCliCommands.SetPower(canon, true));
            }
            else
            {
                _sendCliLine(LmsCliCommands.Play(canon));
            }
        }

        public void PowerOff(string mac)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null || !_registry.IsBound(canon) || !_isServerConnected()) return;

            _registry.TryGetCapabilities(canon, out var canPowerOff, out _);
            if (canPowerOff)
            {
                _sendCliLine(LmsCliCommands.SetPower(canon, false));
            }
            else
            {
                _sendCliLine(LmsCliCommands.Stop(canon));
            }
        }

        public void PowerToggle(string mac)
        {
            if (!_registry.TryGetSnapshot(mac, out var snap)) return;
            if (snap.IsPoweredOn) PowerOff(mac);
            else PowerOn(mac);
        }

        public void SendPlayerCommand(string mac, string command)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            var safe = SanitizeCommand(command);
            if (safe.Length == 0) return;
            if (!_registry.IsBound(canon) || !_isServerConnected()) return;

            _sendCliLine(LmsCliCommands.PlayerCommand(canon, safe));
        }

        /// <summary>
        /// Strips control characters from an installer-configured command
        /// fragment and collapses the surrounding whitespace.
        /// </summary>
        /// <remarks>
        /// The LMS CLI is newline-delimited, so a fragment containing CR or LF
        /// would be read by the server as two commands — one configured preset
        /// could then issue an arbitrary second command. Dropping every control
        /// character (rather than splitting on the newline and keeping the
        /// first half) means a malformed value fails visibly as one odd command
        /// instead of silently executing something the installer didn't intend.
        /// </remarks>
        private static string SanitizeCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return string.Empty;

            var sb = new System.Text.StringBuilder(command.Length);
            foreach (var c in command)
            {
                if (!char.IsControl(c)) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        // ===== Commands: Receiver (Driver 4) =====

        public void SetVolume(string mac, int level)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null || !_registry.IsBound(canon) || !_isServerConnected()) return;
            _sendCliLine(LmsCliCommands.SetVolume(canon, level));
        }

        public void VolumeUp(string mac, int step)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null || !_registry.IsBound(canon) || !_isServerConnected()) return;
            _sendCliLine(LmsCliCommands.VolumeUp(canon, step));
        }

        public void VolumeDown(string mac, int step)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null || !_registry.IsBound(canon) || !_isServerConnected()) return;
            _sendCliLine(LmsCliCommands.VolumeDown(canon, step));
        }

        public void SetMute(string mac, bool muted)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null || !_registry.IsBound(canon) || !_isServerConnected()) return;
            _sendCliLine(LmsCliCommands.SetMute(canon, muted));
        }

        // A configuration publish (not an LMS command): stores the Receiver's
        // configured step in the registry so other consumers can match it. No
        // connectivity gate — it updates local state only. NoteVolumeStep is a
        // no-op for an unbound MAC.
        public void SetVolumeStep(string mac, int step) => _registry.NoteVolumeStep(mac, step);

        // ===== Internals =====

        private void SendForPlayer(string mac, Func<string, string> commandBuilder)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null || !_registry.IsBound(canon) || !_isServerConnected()) return;
            _sendCliLine(commandBuilder(canon));
        }
    }
}
