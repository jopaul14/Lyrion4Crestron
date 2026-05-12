// ---------------------------------------------------------------------------
//  Gateway_Lyrion_LMS_IP - Lyrion Server gateway driver (Driver 1 of 3)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using LyrionCommunity.Crestron.Lyrion.Gateway.Protocol;
using LyrionCommunity.Crestron.Lyrion.Gateway.Registry;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Gateway.Services
{
    /// <summary>
    /// Implementation of the cross-driver gateway service. Commands are
    /// translated into LMS CLI lines and dispatched via the supplied sender;
    /// events are forwarded from the registry. Commands issued while the
    /// gateway is not CONNECTED are dropped (per CLAUDE.md "Commands dropped
    /// when server is not connected").
    /// </summary>
    internal sealed class LyrionGatewayServiceImpl : ILyrionGatewayService
    {
        private readonly PlayerRegistry _registry;
        private readonly Func<string, bool> _sendCliLine;
        private readonly Func<bool> _isServerConnected;
        private readonly Action<string> _onPlayerBound;

        public LyrionGatewayServiceImpl(
            PlayerRegistry registry,
            Func<string, bool> sendCliLine,
            Func<bool> isServerConnected,
            Action<string> onPlayerBound)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _sendCliLine = sendCliLine ?? throw new ArgumentNullException(nameof(sendCliLine));
            _isServerConnected = isServerConnected ?? throw new ArgumentNullException(nameof(isServerConnected));
            _onPlayerBound = onPlayerBound ?? (_ => { });

            registry.AvailabilityChanged += (mac, b) => AvailabilityChanged?.Invoke(mac, b);
            registry.PowerStateChanged += (mac, b) => PowerStateChanged?.Invoke(mac, b);
            registry.PlaybackStateChanged += (mac, s) => PlaybackStateChanged?.Invoke(mac, s);
            registry.MetadataUpdated += (mac, m) => MetadataUpdated?.Invoke(mac, m);
            registry.ShuffleChanged += (mac, b) => ShuffleChanged?.Invoke(mac, b);
            registry.RepeatChanged += (mac, b) => RepeatChanged?.Invoke(mac, b);
            registry.VolumeChanged += (mac, v) => VolumeChanged?.Invoke(mac, v);
            registry.MuteChanged += (mac, b) => MuteChanged?.Invoke(mac, b);
            registry.PresetsUpdated += (mac, p) => PresetsUpdated?.Invoke(mac, p);
        }

        // ===== Bind / snapshot =====

        public bool BindPlayer(string mac)
        {
            if (!_registry.Bind(mac)) return false;
            try { _onPlayerBound(MacAddress.Normalize(mac)); }
            catch { }
            return true;
        }

        public void UnbindPlayer(string mac) => _registry.Unbind(mac);

        public bool TryGetSnapshot(string mac, out LyrionPlayerSnapshot snapshot)
            => _registry.TryGetSnapshot(mac, out snapshot);

        // ===== Events =====

        public event Action<string, bool> AvailabilityChanged;
        public event Action<string, bool> PowerStateChanged;
        public event Action<string, LyrionPlaybackState> PlaybackStateChanged;
        public event Action<string, LyrionMetadata> MetadataUpdated;
        public event Action<string, bool> ShuffleChanged;
        public event Action<string, bool> RepeatChanged;
        public event Action<string, int> VolumeChanged;
        public event Action<string, bool> MuteChanged;
        public event Action<string, IReadOnlyList<LyrionPreset>> PresetsUpdated;

        // ===== Commands: Media (Driver 2) =====

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

            // Players that don't accept power-off treat power-on as a play
            // resumption; fall back silently per CLAUDE.md appendix.
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

        public void ActivatePreset(string mac, string presetId)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null || string.IsNullOrEmpty(presetId)) return;
            if (!_registry.IsBound(canon) || !_isServerConnected()) return;
            _sendCliLine(LmsCliCommands.ActivatePreset(canon, presetId));
        }

        // ===== Commands: Volume (Driver 3) =====

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

        // ===== Internals =====

        private void SendForPlayer(string mac, Func<string, string> commandBuilder)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null || !_registry.IsBound(canon) || !_isServerConnected()) return;
            _sendCliLine(commandBuilder(canon));
        }
    }
}
