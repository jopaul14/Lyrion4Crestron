// ---------------------------------------------------------------------------
//  Receiver_Lyrion_Player - Lyrion Receiver (Driver 4 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using Crestron.RAD.Common.Transports;
using Crestron.RAD.DeviceTypes.RADAVReceiver;

namespace LyrionCommunity.Crestron.Lyrion.Receiver
{
    internal sealed class ReceiverProtocol : AAVReceiverProtocol
    {
        internal const string MacAttributeId = "MacAddress";
        internal const string VolumeStepAttributeId = "VolumeStep";

        public event Action<string> MacAddressReceived;
        public event Action<int> VolumeStepReceived;
        public event Action PowerOnRequested;
        public event Action PowerOffRequested;
        public event Action PowerToggleRequested;
        public event Action MuteOnRequested;
        public event Action MuteOffRequested;
        public event Action<uint> SetVolumeRequested;
        public event Action VolumeUpRequested;
        public event Action VolumeDownRequested;

        public ReceiverProtocol(ISerialTransport transport, byte id)
            : base(transport, id)
        {
        }

        public override void SetUserAttribute(string attributeId, string attributeValue)
        {
            if (string.Equals(attributeId, MacAttributeId, StringComparison.OrdinalIgnoreCase))
            {
                MacAddressReceived?.Invoke(attributeValue);
            }
            else if (string.Equals(attributeId, VolumeStepAttributeId, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(attributeValue, out var step) && step >= 1 && step <= 50)
                {
                    VolumeStepReceived?.Invoke(step);
                }
            }
        }

        public override void PowerOn() => PowerOnRequested?.Invoke();
        public override void PowerOff() => PowerOffRequested?.Invoke();
        public override void Power() => PowerToggleRequested?.Invoke();

        public override void MuteOn() => MuteOnRequested?.Invoke();
        public override void MuteOff() => MuteOffRequested?.Invoke();

        public override void SetVolume(uint volume) => SetVolumeRequested?.Invoke(volume);
        public override void PressVolumeUp() => VolumeUpRequested?.Invoke();
        public override void PressVolumeDown() => VolumeDownRequested?.Invoke();
    }
}
