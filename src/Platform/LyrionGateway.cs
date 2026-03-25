using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.DeviceTypes.Gateway;
using Crestron.SimplSharp;
using Lyrion4Crestron.Common;
 
namespace Lyrion4Crestron.Platform
{
    /// <summary>
    /// Main gateway driver for Lyrion Music Server.
    /// Connects to the LMS CLI interface via TCP and discovers Squeezebox players
    /// as paired extension devices.
    /// </summary>
    public class LyrionGateway : AGateway, ITcp
    {
        public void Initialize(IPAddress ipAddress, int port)
        {
            var transport = new LyrionGatewayTcpTransport
            {
                EnableLogging = InternalEnableLogging,
                CustomLogger = InternalCustomLogger,
                EnableRxDebug = InternalEnableRxDebug,
                EnableTxDebug = InternalEnableTxDebug,
                Hostname = ipAddress.ToString(),
                Port = port > 0 ? port : LyrionConstants.DefaultCliPort
            };
 
            ConnectionTransport = transport;
 
            Protocol = new LyrionGatewayProtocol(ConnectionTransport, Id)
            {
                EnableLogging = InternalEnableLogging,
                CustomLogger = InternalCustomLogger
            };
        }
 
        public override void Connect()
        {
            base.Connect();
            ((LyrionGatewayProtocol)Protocol).Connect();
        }
 
        public override void Disconnect()
        {
            base.Disconnect();
            ((LyrionGatewayProtocol)Protocol).Disconnect();
        }
    }
}