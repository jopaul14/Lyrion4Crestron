using Crestron.RAD.Common.Transports;
using Crestron.SimplSharp;
using Lyrion4Crestron.Common;
 
namespace Lyrion4Crestron.Platform
{
    /// <summary>
    /// TCP transport driver for communicating with Lyrion Music Server's CLI interface.
    /// Handles sending commands and dispatching responses back to the protocol layer.
    /// </summary>
    public class LyrionGatewayTcpTransport : ATransportDriver
    {
        private TCPClient _tcpClient;
        private string _hostname;
        private int _port;
        private readonly CriticalSection _sendLock = new CriticalSection();
        private byte[] _receiveBuffer = new byte[4096];
        private string _partialResponse = string.Empty;
 
        public string Hostname
        {
            get { return _hostname; }
            set { _hostname = value; }
        }
 
        public int Port
        {
            get { return _port; }
            set { _port = value; }
        }
 
        public override void SendMethod(string message, object[] parameters)
        {
            if (_tcpClient == null || _tcpClient.ClientStatus != SocketStatus.SOCKET_STATUS_CONNECTED)
                return;
 
            try
            {
                _sendLock.Enter();
                var data = System.Text.Encoding.UTF8.GetBytes(message + LyrionConstants.Delimiter);
                _tcpClient.SendData(data, data.Length);
            }
            finally
            {
                _sendLock.Leave();
            }
        }
 
        public override void Start()
        {
            if (_tcpClient != null)
            {
                Stop();
            }
 
            _tcpClient = new TCPClient(_hostname, _port, 4096);
            _tcpClient.SocketStatusChange += OnSocketStatusChange;
 
            var result = _tcpClient.ConnectToServerAsync(OnConnectCallback);
            if (result != SocketErrorCodes.SOCKET_OK &&
                result != SocketErrorCodes.SOCKET_CONNECTION_IN_PROGRESS)
            {
                ConnectionChanged(false);
            }
        }
 
        public override void Stop()
        {
            if (_tcpClient != null)
            {
                _tcpClient.SocketStatusChange -= OnSocketStatusChange;
                _tcpClient.DisconnectFromServer();
                _tcpClient.Dispose();
                _tcpClient = null;
            }
            _partialResponse = string.Empty;
            ConnectionChanged(false);
        }
 
        private void OnConnectCallback(TCPClient client)
        {
            if (client.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                ConnectionChanged(true);
                client.ReceiveDataAsync(OnDataReceived);
            }
            else
            {
                ConnectionChanged(false);
            }
        }
 
        private void OnSocketStatusChange(TCPClient client, SocketStatus status)
        {
            ConnectionChanged(status == SocketStatus.SOCKET_STATUS_CONNECTED);
        }
 
        private void OnDataReceived(TCPClient client, int bytesReceived)
        {
            if (bytesReceived <= 0)
            {
                ConnectionChanged(false);
                return;
            }
 
            var data = System.Text.Encoding.UTF8.GetString(
                client.IncomingDataBuffer, 0, bytesReceived);
 
            _partialResponse += data;
 
            // Process complete lines (LMS CLI uses newline-delimited responses)
            while (true)
            {
                var newlineIndex = _partialResponse.IndexOf('\n');
                if (newlineIndex < 0)
                    break;
 
                var line = _partialResponse.Substring(0, newlineIndex).Trim();
                _partialResponse = _partialResponse.Substring(newlineIndex + 1);
 
                if (!string.IsNullOrEmpty(line))
                {
                    DataHandler(line);
                }
            }
 
            // Continue receiving
            if (client.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                client.ReceiveDataAsync(OnDataReceived);
            }
        }
    }
}