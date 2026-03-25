using System;
using Crestron.RAD.Common.BasicDriver;
using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.DeviceTypes.ExtensionDevice;
 
namespace Lyrion4Crestron.PairedDevices
{
    /// <summary>
    /// Represents an individual Lyrion/Squeezebox player as a Crestron paired extension device.
    /// Each player discovered by the gateway becomes an instance of this class.
    /// </summary>
    public class LyrionPlayerExtension : AExtensionDevice, IPairedDevice
    {
        private readonly PairedDeviceInformation _pairedDeviceInformation;
        private readonly LyrionPlayerProtocol _playerProtocol;
 
        public LyrionPlayerExtension(string playerId, string playerName)
        {
            _pairedDeviceInformation = new PairedDeviceInformation
            {
                Id = playerId,
                Name = playerName,
                Description = "Lyrion Music Player",
                Manufacturer = "Lyrion Music Server",
                DeviceType = "MediaPlayer"
            };
 
            _playerProtocol = new LyrionPlayerProtocol(null, Id)
            {
                PlayerId = playerId
            };
 
            Protocol = _playerProtocol;
        }
 
        #region IPairedDevice
 
        public PairedDeviceInformation PairedDeviceInformation
        {
            get { return _pairedDeviceInformation; }
        }
 
        public void SetConnectionStatus(bool isConnected)
        {
            Protocol.IsConnected = isConnected;
        }
 
        #endregion
 
        /// <summary>
        /// Gets the underlying protocol for direct state updates from the gateway.
        /// </summary>
        public LyrionPlayerProtocol PlayerProtocol
        {
            get { return _playerProtocol; }
        }
    }
}