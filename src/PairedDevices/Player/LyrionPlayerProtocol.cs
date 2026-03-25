using System;
using Crestron.RAD.Common.BasicDriver;
using Crestron.RAD.Common.Enums;
using Crestron.RAD.Common.Transports;
using Crestron.RAD.DeviceTypes.ExtensionDevice;
 
namespace Lyrion4Crestron.PairedDevices
{
    /// <summary>
    /// Protocol handler for an individual Lyrion/Squeezebox player extension device.
    /// Manages transport control, volume, and now-playing state.
    /// </summary>
    public class LyrionPlayerProtocol : AExtensionDeviceProtocol
    {
        private string _playerId;
        private bool _isPlaying;
        private bool _isPaused;
        private string _currentTitle = string.Empty;
        private string _currentArtist = string.Empty;
        private string _currentAlbum = string.Empty;
        private int _volume;
        private bool _isMuted;
        private bool _isPowered;
 
        public LyrionPlayerProtocol(ISerialTransport transport, byte id)
            : base(transport, id)
        {
        }
 
        /// <summary>
        /// Gets or sets the player MAC address used to target CLI commands.
        /// </summary>
        public string PlayerId
        {
            get { return _playerId; }
            set { _playerId = value; }
        }
 
        #region Extension Device Attributes
 
        public bool IsPlaying
        {
            get { return _isPlaying; }
        }
 
        public bool IsPaused
        {
            get { return _isPaused; }
        }
 
        public string CurrentTitle
        {
            get { return _currentTitle; }
        }
 
        public string CurrentArtist
        {
            get { return _currentArtist; }
        }
 
        public string CurrentAlbum
        {
            get { return _currentAlbum; }
        }
 
        public int Volume
        {
            get { return _volume; }
        }
 
        public bool IsMuted
        {
            get { return _isMuted; }
        }
 
        public bool IsPowered
        {
            get { return _isPowered; }
        }
 
        #endregion
 
        #region Transport Controls
 
        public void Play()
        {
            SendPlayerCommand(Common.LyrionConstants.PlayCommand);
        }
 
        public void Pause()
        {
            SendPlayerCommand(Common.LyrionConstants.PauseCommand);
        }
 
        public void Stop()
        {
            SendPlayerCommand(Common.LyrionConstants.StopCommand);
        }
 
        public void NextTrack()
        {
            SendPlayerCommand(Common.LyrionConstants.NextTrackCommand);
        }
 
        public void PreviousTrack()
        {
            SendPlayerCommand(Common.LyrionConstants.PreviousTrackCommand);
        }
 
        #endregion
 
        #region Volume Controls
 
        public void SetVolume(int level)
        {
            if (level < 0) level = 0;
            if (level > 100) level = 100;
            SendPlayerCommand(string.Format("{0} {1}", Common.LyrionConstants.VolumeSetCommand, level));
        }
 
        public void VolumeUp()
        {
            SendPlayerCommand(string.Format("{0} +5", Common.LyrionConstants.VolumeSetCommand));
        }
 
        public void VolumeDown()
        {
            SendPlayerCommand(string.Format("{0} -5", Common.LyrionConstants.VolumeSetCommand));
        }
 
        public void ToggleMute()
        {
            SendPlayerCommand(Common.LyrionConstants.MuteToggleCommand);
        }
 
        #endregion
 
        #region Power Controls
 
        public void PowerOn()
        {
            SendPlayerCommand(Common.LyrionConstants.PowerOnCommand);
        }
 
        public void PowerOff()
        {
            SendPlayerCommand(Common.LyrionConstants.PowerOffCommand);
        }
 
        #endregion
 
        #region State Updates
 
        /// <summary>
        /// Updates the player state from a LyrionPlayerInfo object.
        /// Called by the gateway protocol when player status changes are received.
        /// </summary>
        public void UpdatePlayerState(Common.LyrionPlayerInfo playerInfo)
        {
            if (playerInfo == null)
                return;
 
            _volume = playerInfo.Volume;
            _isMuted = playerInfo.IsMuted;
            _isPowered = playerInfo.IsPowered;
            _currentTitle = playerInfo.CurrentTitle ?? string.Empty;
            _currentArtist = playerInfo.CurrentArtist ?? string.Empty;
            _currentAlbum = playerInfo.CurrentAlbum ?? string.Empty;
 
            if (playerInfo.Mode == "play")
            {
                _isPlaying = true;
                _isPaused = false;
            }
            else if (playerInfo.Mode == "pause")
            {
                _isPlaying = false;
                _isPaused = true;
            }
            else
            {
                _isPlaying = false;
                _isPaused = false;
            }
 
            Commit();
        }
 
        #endregion
 
        #region Base Members
 
        protected override void ChooseDeconstructMethod(ValidatedRxData validatedData)
        {
            // Responses are handled by the gateway protocol which calls UpdatePlayerState
        }
 
        protected override void ConnectionChangedEvent(bool connection)
        {
            base.ConnectionChangedEvent(connection);
        }
 
        #endregion
 
        #region Private Methods
 
        private void SendPlayerCommand(string command)
        {
            if (string.IsNullOrEmpty(_playerId))
                return;
 
            var encodedPlayerId = Common.LyrionResponseParser.UrlEncode(_playerId);
            var fullCommand = string.Format("{0} {1}", encodedPlayerId, command);
 
            var commandSet = new CommandSet(
                fullCommand,
                fullCommand,
                CommonCommandGroupType.Other,
                null,
                false,
                CommandPriority.Normal,
                StandardCommandsEnum.NotAStandardCommand);
 
            SendCommand(commandSet);
        }
 
        #endregion
    }
}