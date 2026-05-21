// ---------------------------------------------------------------------------
//  Helper_Lyrion_Player - Lyrion Helper (Driver 3 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using Crestron.RAD.Common.BasicDriver;
using Crestron.RAD.Common.Transports;

namespace LyrionCommunity.Crestron.Lyrion.Helper
{
    internal sealed class HelperProtocol : ABaseDriverProtocol
    {
        internal const string MacAttributeId = "MacAddress";

        public event Action<string> MacAddressReceived;

        public HelperProtocol(ISerialTransport transport, byte id)
            : base(transport, id)
        {
        }

        protected override void ConnectionChangedEvent(bool connection) { }
        protected override void ChooseDeconstructMethod(ValidatedRxData validatedData) { }

        public override void SetUserAttribute(string attributeId, string attributeValue)
        {
            if (string.Equals(attributeId, MacAttributeId, StringComparison.OrdinalIgnoreCase))
            {
                MacAddressReceived?.Invoke(attributeValue);
            }
        }

        public override void SetUserAttribute(string attributeId, bool attributeValue) { }
        public override void SetUserAttribute(string attributeId, ushort attributeValue) { }
    }
}
