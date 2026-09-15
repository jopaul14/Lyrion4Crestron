// ---------------------------------------------------------------------------
//  Receiver_Lyrion_Player - Lyrion Receiver (Driver 4 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Globalization;
using Crestron.RAD.Common.Transports;
using Crestron.RAD.DeviceTypes.RADAVReceiver;

namespace LyrionCommunity.Crestron.Lyrion.Receiver
{
    /// <summary>
    /// Minimal AV-receiver protocol. There is no device protocol — the
    /// transport is a no-op and every command is forwarded to the driver as
    /// an event, which routes it to the Lyrion Server service. Exists to
    /// satisfy the RAD framework and to surface the two user attributes.
    /// </summary>
    internal sealed class ReceiverProtocol : AAVReceiverProtocol
    {
        internal const string MacAttributeId = "MacAddress";
        internal const string VolumeStepAttributeId = "VolumeStep";

        /// <summary>Volume step used when the attribute is blank or invalid (Driver.json says "default 2").</summary>
        internal const int DefaultVolumeStep = 2;
        internal const int MinVolumeStep = 1;
        internal const int MaxVolumeStep = 50;

        public event Action<string> MacAddressReceived;

        /// <summary>
        /// The effective volume step, plus the raw attribute text when that
        /// text was NOT a valid step (null when it was). An invalid or cleared
        /// value falls back to <see cref="DefaultVolumeStep"/> rather than
        /// silently keeping the previous step, so the driver behaves the same
        /// before and after a reload for the same persisted value.
        /// </summary>
        public event Action<int, string> VolumeStepReceived;

        public event Action PowerOnRequested;
        public event Action PowerOffRequested;
        public event Action PowerToggleRequested;
        public event Action MuteOnRequested;
        public event Action MuteOffRequested;
        public event Action<uint> SetVolumeRequested;
        public event Action VolumeUpRequested;
        public event Action VolumeDownRequested;
        public event Action VolumeReleaseRequested;

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
                if (int.TryParse(attributeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var step)
                    && step >= MinVolumeStep && step <= MaxVolumeStep)
                {
                    VolumeStepReceived?.Invoke(step, null);
                }
                else
                {
                    VolumeStepReceived?.Invoke(DefaultVolumeStep, attributeValue ?? string.Empty);
                }
            }
        }

        public override void PowerOn() => PowerOnRequested?.Invoke();
        public override void PowerOff() => PowerOffRequested?.Invoke();
        public override void Power() => PowerToggleRequested?.Invoke();

        public override void MuteOn() => MuteOnRequested?.Invoke();
        public override void MuteOff() => MuteOffRequested?.Invoke();

        public override void SetVolume(uint volume) => SetVolumeRequested?.Invoke(volume);

        // Volume ramp. Crestron Home delivers a held volume button as a Press
        // followed by a Release (ABasicAVReceiver.VolumeUp(CommandAction) →
        // PressVolumeUp / ReleaseVolume; a tap is the pair back to back). The
        // base class ramps between the two by re-sending its own standard
        // command on a timer AND fabricating VolumeIs feedback each tick,
        // which would fight the real feedback coming back from LMS — so the
        // base is bypassed here and the driver runs its own repeat between
        // these events. Through 1.0.14 Release was not forwarded at all, so a
        // hold moved the volume by exactly one step.
        public override void PressVolumeUp() => VolumeUpRequested?.Invoke();
        public override void PressVolumeDown() => VolumeDownRequested?.Invoke();
        public override void ReleaseVolume() => VolumeReleaseRequested?.Invoke();
    }
}
