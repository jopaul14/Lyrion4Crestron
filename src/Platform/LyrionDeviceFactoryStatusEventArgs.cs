using System;
using Crestron.RAD.Common.Interfaces;
 
namespace Lyrion4Crestron.Platform
{
    /// <summary>
    /// Event args for device factory status changes (player added/removed/updated).
    /// </summary>
    public class LyrionDeviceFactoryStatusEventArgs : EventArgs
    {
        public DeviceStatus Status { get; private set; }
        public IPairedDevice Device { get; private set; }
 
        public LyrionDeviceFactoryStatusEventArgs(DeviceStatus status, IPairedDevice device)
        {
            Status = status;
            Device = device;
        }
    }
 
    internal enum DeviceStatus
    {
        Added,
        Removed,
        Updated
    }
}