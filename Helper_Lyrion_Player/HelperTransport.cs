// ---------------------------------------------------------------------------
//  Helper_Lyrion_Player - Lyrion Helper (Driver 3 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using Crestron.RAD.Common.Transports;

namespace LyrionCommunity.Crestron.Lyrion.Helper
{
    internal sealed class HelperTransport : ATransportDriver
    {
        public HelperTransport()
        {
            IsConnected = true;
        }

        public override void SendMethod(string message, object[] paramaters) { }
        public override void Start() { }
        public override void Stop() { }
    }
}
