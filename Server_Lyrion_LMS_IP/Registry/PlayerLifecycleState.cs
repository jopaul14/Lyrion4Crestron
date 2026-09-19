// ---------------------------------------------------------------------------
//  Server_Lyrion_LMS_IP - Lyrion Server driver (Driver 1 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

namespace LyrionCommunity.Crestron.Lyrion.Server.Registry
{
    /// <summary>Lifecycle states per CLAUDE.md "DRIVER 1 INTERNAL PLAYER REGISTRY".</summary>
    internal enum PlayerLifecycleState
    {
        Unknown = 0,
        Offline = 1,
        Online = 2,
        InvalidSession = 3
    }
}
