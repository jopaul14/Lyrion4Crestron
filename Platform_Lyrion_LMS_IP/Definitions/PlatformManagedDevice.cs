// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace LyrionCommunity.Crestron.Lyrion.Definitions
{
    /// <summary>
    /// Describes a sub-device exposed by the Platform driver. The shape mirrors
    /// the canonical <c>platform:ManagedDevice</c> entity data type used by the
    /// Crestron Entity Model.
    /// </summary>
    /// <remarks>
    /// Instances are immutable. The <see cref="Platform"/>-style collection that
    /// contains them must be copy-on-write; never mutate the collection in place.
    /// </remarks>
    [EntityDataType(Id = "platform:ManagedDevice")]
    public sealed class PlatformManagedDevice
    {
        public PlatformManagedDevice(
            DeviceUxCategory uxCategory,
            string name,
            string manufacturer,
            string model,
            string serialNumber)
        {
            UxCategory = uxCategory;
            Name = name;
            Manufacturer = manufacturer;
            Model = model;
            SerialNumber = serialNumber;
        }

        [EntityProperty]
        public DeviceUxCategory UxCategory { get; }

        [EntityProperty]
        public string Name { get; }

        [EntityProperty]
        public string Manufacturer { get; }

        [EntityProperty]
        public string Model { get; }

        [EntityProperty]
        public string SerialNumber { get; }
    }
}
