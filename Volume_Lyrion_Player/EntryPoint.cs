// ---------------------------------------------------------------------------
//  Volume_Lyrion_Player - Lyrion Receiver (Driver 3 of 3)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.IO;
using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using LyrionCommunity.Crestron.Lyrion.Volume;

[assembly: DriverAssemblyEntryPoint(typeof(EntryPoint))]

public sealed class EntryPoint : DriverAssemblyEntryPoint
{
    public override DriverController CreateDriverControllerInstance(DriverControllerCreationArgs args)
    {
        try
        {
            var resources = DriverImplementationResources.FromCreationArgs(args, typeof(EntryPoint));
            var driver = new VolumeDriver(args, resources);
            var entity = new ConfigurableDriverEntity(driver.ControllerId, driver, driver.ConfigurationController);
            return new DispatchingDeviceController(entity, args, null);
        }
        catch (Exception ex)
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location) ?? ".";
                File.WriteAllText(Path.Combine(dir, "volume_init_error.log"),
                    DateTime.UtcNow.ToString("o") + Environment.NewLine + ex);
            }
            catch { }
            throw;
        }
    }
}
