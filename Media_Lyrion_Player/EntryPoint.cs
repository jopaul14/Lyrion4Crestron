// ---------------------------------------------------------------------------
//  Media_Lyrion_Player - Lyrion Source (Driver 2 of 3)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using LyrionCommunity.Crestron.Lyrion.Media;

[assembly: DriverAssemblyEntryPoint(typeof(EntryPoint))]

public sealed class EntryPoint : DriverAssemblyEntryPoint
{
    public override DriverController CreateDriverControllerInstance(DriverControllerCreationArgs args)
    {
        var resources = DriverImplementationResources.FromCreationArgs(args, typeof(EntryPoint));
        var driver = new MediaDriver(args, resources);
        var entity = new ConfigurableDriverEntity(driver.ControllerId, driver, driver.ConfigurationController);
        return new DispatchingDeviceController(entity, args, null);
    }
}
