// ---------------------------------------------------------------------------
//  Gateway_Lyrion_LMS_IP - Lyrion Server gateway driver (Driver 1 of 3)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.IO;
using System.Reflection;
using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;

[assembly: DriverAssemblyEntryPoint(typeof(EntryPoint))]

public sealed class EntryPoint : DriverAssemblyEntryPoint
{
    static EntryPoint()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
    }

    private static Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs args)
    {
        var requested = new AssemblyName(args.Name).Name;
        if (requested != "Lyrion_Common") return null;

        var self = typeof(EntryPoint).Assembly;
        using (var stream = self.GetManifestResourceStream("Lyrion_Common.dll"))
        {
            if (stream == null) return null;
            var bytes = new byte[stream.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = stream.Read(bytes, read, bytes.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return Assembly.Load(bytes);
        }
    }

    public override DriverController CreateDriverControllerInstance(DriverControllerCreationArgs args)
    {
        try
        {
            return CreateImpl(args);
        }
        catch (Exception ex)
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location) ?? ".";
                File.WriteAllText(Path.Combine(dir, "gateway_init_error.log"),
                    DateTime.UtcNow.ToString("o") + Environment.NewLine + ex);
            }
            catch { }
            throw;
        }
    }

    private static DriverController CreateImpl(DriverControllerCreationArgs args)
    {
        var resources = DriverImplementationResources.FromCreationArgs(args, typeof(EntryPoint));
        var driver = new LyrionCommunity.Crestron.Lyrion.Gateway.GatewayDriver(args, resources);
        var entity = new ConfigurableDriverEntity(driver.ControllerId, driver, driver.ConfigurationController);
        return new DispatchingDeviceController(entity, args, null);
    }
}
