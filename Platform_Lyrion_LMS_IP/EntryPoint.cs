// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using LyrionCommunity.Crestron.Lyrion;

// Tells the Crestron driver host where to start when loading this assembly.
[assembly: DriverAssemblyEntryPoint(typeof(EntryPoint))]

/// <summary>
/// Assembly entry point wired up via <see cref="DriverAssemblyEntryPoint"/>.
/// </summary>
/// <remarks>
/// The class is declared in the global namespace to match the canonical
/// Crestron V2 sample (see <c>Platform_Sample_TutorialLights_IP</c>).
/// Construction does the minimum required to build a
/// <see cref="DispatchingDeviceController"/> around the root
/// <see cref="DriverMain"/> entity; all work is driven from configuration.
/// </remarks>
public sealed class EntryPoint : DriverAssemblyEntryPoint
{
    public override DriverController CreateDriverControllerInstance(DriverControllerCreationArgs args)
    {
        // Parse Driver.json and any other embedded resources for this driver.
        var resources = DriverImplementationResources.FromCreationArgs(args, typeof(EntryPoint));

        // Root entity. Owns the transport clients and the ManagedDevices collection.
        var driverEntity = new DriverMain(args, resources);

        // Wrap the entity together with its configuration controller so the
        // DispatchingDeviceController can route config deltas to the entity.
        var entity = new ConfigurableDriverEntity(
            driverEntity.ControllerId,
            driverEntity,
            driverEntity.ConfigurationController);

        // DispatchingDeviceController handles routing of property/command/event
        // traffic to sub-entities registered via UpdateSubControllers(...).
        return new DispatchingDeviceController(entity, args, null);
    }
}
