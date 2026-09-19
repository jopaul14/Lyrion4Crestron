// ---------------------------------------------------------------------------
//  Source_Lyrion_Player - Lyrion Source (Driver 2 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using Crestron.RAD.Common.Transports;

namespace LyrionCommunity.Crestron.Lyrion.Source
{
    /// <summary>
    /// No-op transport. The Source driver never talks to LMS directly — all
    /// control flows through the Lyrion Server service. The RAD
    /// framework still requires a transport, so this one reports itself as
    /// permanently connected and discards everything written to it.
    /// </summary>
    internal sealed class SourceTransport : ATransportDriver
    {
        public SourceTransport()
        {
            IsConnected = true;
        }

        public override void SendMethod(string message, object[] paramaters) { }
        public override void Start() { }
        public override void Stop() { }
    }
}
