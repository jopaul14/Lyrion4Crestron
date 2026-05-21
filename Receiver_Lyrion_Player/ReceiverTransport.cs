// ---------------------------------------------------------------------------
//  Receiver_Lyrion_Player - Lyrion Receiver (Driver 4 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using Crestron.RAD.Common.Transports;

namespace LyrionCommunity.Crestron.Lyrion.Receiver
{
    internal sealed class ReceiverTransport : ATransportDriver
    {
        public ReceiverTransport()
        {
            IsConnected = true;
        }

        public override void SendMethod(string message, object[] paramaters) { }
        public override void Start() { }
        public override void Stop() { }
    }
}
