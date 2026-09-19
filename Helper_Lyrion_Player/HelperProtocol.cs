// ---------------------------------------------------------------------------
//  Helper_Lyrion_Player - Lyrion Helper (Driver 3 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using Crestron.RAD.Common.BasicDriver;
using Crestron.RAD.Common.Transports;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Helper
{
    internal sealed class HelperProtocol : ABaseDriverProtocol
    {
        internal const string MacAttributeId = "MacAddress";

        /// <summary>Number of configurable preset slots (Preset1 … Preset4).</summary>
        internal const int PresetCount = 4;

        private const string PresetAttributePrefix = "Preset";

        public event Action<string> MacAddressReceived;

        /// <summary>
        /// Raised when the installer edits a preset slot. Arguments are the
        /// zero-based slot index and the raw configured string (the
        /// <c>Name|Icon|Command</c> form; parsing lives in
        /// <see cref="LyrionPresetConfig.Parse"/>).
        /// </summary>
        public event Action<int, string> PresetReceived;

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
                return;
            }

            var slot = PresetSlot(attributeId);
            if (slot >= 0)
            {
                PresetReceived?.Invoke(slot, attributeValue);
            }
        }

        /// <summary>
        /// Maps "Preset1".."Preset4" to a zero-based slot index, or -1 for any
        /// other attribute id.
        /// </summary>
        private static int PresetSlot(string attributeId)
        {
            if (attributeId == null) return -1;
            if (attributeId.Length != PresetAttributePrefix.Length + 1) return -1;
            if (!attributeId.StartsWith(PresetAttributePrefix, StringComparison.OrdinalIgnoreCase)) return -1;

            var digit = attributeId[attributeId.Length - 1];
            if (digit < '1' || digit > '0' + PresetCount) return -1;
            return digit - '1';
        }

        public override void SetUserAttribute(string attributeId, bool attributeValue) { }
        public override void SetUserAttribute(string attributeId, ushort attributeValue) { }
    }
}
