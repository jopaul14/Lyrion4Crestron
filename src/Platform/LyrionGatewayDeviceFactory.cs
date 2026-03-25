using System;
using System.Collections.Generic;
using Crestron.SimplSharp;
using Lyrion4Crestron.Common;
using Lyrion4Crestron.PairedDevices;
 
namespace Lyrion4Crestron.Platform
{
    /// <summary>
    /// Discovers Lyrion/Squeezebox players from server status responses and
    /// creates/updates/removes paired extension devices accordingly.
    /// </summary>
    public class LyrionGatewayDeviceFactory
    {
        private readonly Dictionary<string, LyrionPlayerExtension> _knownPlayers =
            new Dictionary<string, LyrionPlayerExtension>();
 
        private readonly CCriticalSection _playersLock = new CCriticalSection();
 
        internal event EventHandler<LyrionDeviceFactoryStatusEventArgs> DeviceStatusChanged;
 
        /// <summary>
        /// Processes a serverstatus response from LMS and raises DeviceStatusChanged
        /// events for any new, updated, or removed players.
        /// </summary>
        public void ProcessServerStatus(string response)
        {
            var discoveredPlayers = LyrionResponseParser.ParseServerStatus(response);
            var discoveredIds = new HashSet<string>();
 
            foreach (var playerInfo in discoveredPlayers)
            {
                if (string.IsNullOrEmpty(playerInfo.PlayerId))
                    continue;
 
                discoveredIds.Add(playerInfo.PlayerId);
 
                try
                {
                    _playersLock.Enter();
 
                    if (_knownPlayers.ContainsKey(playerInfo.PlayerId))
                    {
                        // Update existing player
                        var existing = _knownPlayers[playerInfo.PlayerId];
                        existing.PlayerProtocol.UpdatePlayerState(playerInfo);
 
                        if (DeviceStatusChanged != null)
                            DeviceStatusChanged(this,
                                new LyrionDeviceFactoryStatusEventArgs(DeviceStatus.Updated, existing));
                    }
                    else
                    {
                        // New player discovered
                        var playerName = !string.IsNullOrEmpty(playerInfo.Name)
                            ? playerInfo.Name
                            : playerInfo.PlayerId;
 
                        var extension = new LyrionPlayerExtension(playerInfo.PlayerId, playerName);
                        extension.PlayerProtocol.UpdatePlayerState(playerInfo);
                        _knownPlayers[playerInfo.PlayerId] = extension;
 
                        if (DeviceStatusChanged != null)
                            DeviceStatusChanged(this,
                                new LyrionDeviceFactoryStatusEventArgs(DeviceStatus.Added, extension));
                    }
                }
                finally
                {
                    _playersLock.Leave();
                }
            }
 
            // Remove players that are no longer reported by the server
            var toRemove = new List<string>();
            try
            {
                _playersLock.Enter();
                foreach (var id in _knownPlayers.Keys)
                {
                    if (!discoveredIds.Contains(id))
                        toRemove.Add(id);
                }
            }
            finally
            {
                _playersLock.Leave();
            }
 
            foreach (var id in toRemove)
            {
                LyrionPlayerExtension removed;
                try
                {
                    _playersLock.Enter();
                    if (_knownPlayers.TryGetValue(id, out removed))
                        _knownPlayers.Remove(id);
                    else
                        continue;
                }
                finally
                {
                    _playersLock.Leave();
                }
 
                if (DeviceStatusChanged != null)
                    DeviceStatusChanged(this,
                        new LyrionDeviceFactoryStatusEventArgs(DeviceStatus.Removed, removed));
            }
        }
 
        /// <summary>
        /// Updates the state of a specific player from a player status response.
        /// </summary>
        public void UpdatePlayerStatus(string playerId, string response)
        {
            LyrionPlayerExtension player;
            try
            {
                _playersLock.Enter();
                if (!_knownPlayers.TryGetValue(playerId, out player))
                    return;
            }
            finally
            {
                _playersLock.Leave();
            }
 
            var playerInfo = new LyrionPlayerInfo { PlayerId = playerId };
            LyrionResponseParser.ParsePlayerStatus(response, playerInfo);
            player.PlayerProtocol.UpdatePlayerState(playerInfo);
 
            if (DeviceStatusChanged != null)
                DeviceStatusChanged(this,
                    new LyrionDeviceFactoryStatusEventArgs(DeviceStatus.Updated, player));
        }
 
        public void Dispose()
        {
            try
            {
                _playersLock.Enter();
                foreach (var player in _knownPlayers.Values)
                {
                    if (player is IDisposable)
                        ((IDisposable)player).Dispose();
                }
                _knownPlayers.Clear();
            }
            finally
            {
                _playersLock.Leave();
            }
        }
    }
}