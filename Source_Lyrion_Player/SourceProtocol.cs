// ---------------------------------------------------------------------------
//  Source_Lyrion_Player - Lyrion Source (Driver 2 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using Crestron.RAD.Common.BasicDriver;
using Crestron.RAD.Common.Transports;
using Crestron.RAD.DeviceTypes.BlurayPlayer;

namespace LyrionCommunity.Crestron.Lyrion.Source
{
    /// <summary>
    /// Minimal Bluray-player protocol. There is no real device protocol — the
    /// transport is a no-op and all state comes from the Lyrion Server service. This
    /// class exists only to satisfy the RAD framework and to surface the
    /// configured MAC address (delivered via <see cref="SetUserAttribute"/>)
    /// back to the driver.
    /// </summary>
    internal sealed class SourceProtocol : ABlurayPlayerProtocol
    {
        internal const string MacAttributeId = "MacAddress";

        public SourceProtocol(ISerialTransport transport, byte id)
            : base(transport, id)
        {
        }

        /// <summary>Raised when the installer sets the player MAC address.</summary>
        public event Action<string> MacAddressReceived;

        /// <summary>
        /// Power commands are non-virtual on the driver and delegate down to
        /// the protocol, so power intent is intercepted here and forwarded to
        /// the driver via these events. Transport commands (Play/Pause/Stop/
        /// skip) are virtual on the driver and intercepted there instead.
        /// </summary>
        public event Action PowerOnRequested;
        public event Action PowerOffRequested;
        public event Action PowerToggleRequested;

        public override void SetUserAttribute(string attributeId, string attributeValue)
        {
            if (attributeId == MacAttributeId)
            {
                var handler = MacAddressReceived;
                if (handler != null) handler(attributeValue);
            }
        }

        public override void PowerOn()
        {
            var handler = PowerOnRequested;
            if (handler != null) handler();
        }

        public override void PowerOff()
        {
            var handler = PowerOffRequested;
            if (handler != null) handler();
        }

        public override void Power()
        {
            var handler = PowerToggleRequested;
            if (handler != null) handler();
        }
    }
}
