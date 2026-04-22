// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace LyrionCommunity.Crestron.Lyrion.Definitions
{
    /// <summary>
    /// Subset of the Crestron platform UX category enum used by this driver.
    /// The string ID must match the canonical <c>crestron:DeviceUxCategory</c>
    /// type so the root driver can report each managed device correctly.
    /// </summary>
    /// <remarks>
    /// Lyrion players are reported as <see cref="Speaker"/>. <see cref="Other"/>
    /// is included only for fallback paths; the full Crestron enum has many more
    /// values, but we only enumerate the values this driver needs.
    /// </remarks>
    [EntityDataType(Id = "crestron:DeviceUxCategory")]
    public enum DeviceUxCategory
    {
        /// <summary>The device did not fit any predefined category.</summary>
        Other,

        /// <summary>The device is primarily a speaker / network audio player.</summary>
        Speaker
    }
}
