using System;
using System.Collections.Generic;
using Crestron.RAD.Common.BasicDriver;
using Crestron.RAD.Common.Enums;
using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.Common.Transports;
using Crestron.RAD.DeviceTypes.Gateway;
using Crestron.SimplSharp;
using Lyrion4Crestron.Common;
 
namespace Lyrion4Crestron.Platform
{
    /// <summary>
    /// Gateway protocol for Lyrion Music Server. Manages the CLI session,
    /// discovers players via serverstatus, subscribes to notifications,
    /// and routes player status updates to paired extension devices.
    /// </summary>
    public class LyrionGatewayProtocol : AGatewayProtocol
    {
        #region Fields
 
        private readonly Dictionary<string, IPairedDevice> _pairedDevices =
            new Dictionary<string, IPairedDevice>();
 
        private readonly LyrionGatewayDeviceFactory _deviceFactory;
        private readonly CCriticalSection _pairedDevicesLock = new CCriticalSection();
        private CTimer _pollTimer;
        private const uint PollIntervalMs = 30000;
 
        #endregion
 
        #region Initialization
 
        public LyrionGatewayProtocol(ISerialTransport transport, byte id)
            : base(transport, id)
        {
            ValidateResponse = LyrionValidateResponse;
            _deviceFactory = new LyrionGatewayDeviceFactory();
        }
 
        #endregion
 
        #region Base Members
 
        protected override void ChooseDeconstructMethod(ValidatedRxData validatedData)
        {
            if (string.IsNullOrEmpty(validatedData.Data))
                return;
 
            var response = validatedData.Data;
 
            // Handle serverstatus responses - contains player list
            if (response.StartsWith("serverstatus"))
            {
                _deviceFactory.ProcessServerStatus(response);
                return;
            }
 
            // Handle subscribe confirmation
            if (response.StartsWith("subscribe"))
                return;
 
            // Handle individual player status responses and notifications
            // LMS CLI format: <playerid> <command> <params...>
            var spaceIndex = response.IndexOf(' ');
            if (spaceIndex > 0)
            {
                var possiblePlayerId = LyrionResponseParser.UrlDecode(
                    response.Substring(0, spaceIndex));
 
                // Player IDs are MAC addresses (contain colons)
                if (possiblePlayerId.Contains(":"))
                {
                    var remainder = response.Substring(spaceIndex + 1);
 
                    // If it's a status response, parse it fully
                    if (remainder.StartsWith("status"))
                    {
                        _deviceFactory.UpdatePlayerStatus(possiblePlayerId, remainder);
                    }
                    else if (remainder.StartsWith("playlist") ||
                             remainder.StartsWith("mixer") ||
                             remainder.StartsWith("pause") ||
                             remainder.StartsWith("play") ||
                             remainder.StartsWith("stop") ||
                             remainder.StartsWith("power") ||
                             remainder.StartsWith("client"))
                    {
                        // A notification about a player state change - request fresh status
                        RequestPlayerStatus(possiblePlayerId);
                    }
                }
            }
        }
 
        protected override void ConnectionChangedEvent(bool connection)
        {
            base.ConnectionChangedEvent(connection);
 
            if (connection)
            {
                // Subscribe to notifications and request initial server status
                SendSubscribeCommand();
                SendServerStatusRequest();
                StartPolling();
            }
            else
            {
                StopPolling();
            }
 
            foreach (var pairedDevice in _pairedDevices.Values)
                pairedDevice.SetConnectionStatus(connection);
        }
 
        #endregion
 
        #region Public Members
 
        public void Connect()
        {
            _deviceFactory.DeviceStatusChanged -= Factory_DeviceStatusChanged;
            _deviceFactory.DeviceStatusChanged += Factory_DeviceStatusChanged;
        }
 
        public void Disconnect()
        {
            _deviceFactory.DeviceStatusChanged -= Factory_DeviceStatusChanged;
            StopPolling();
        }
 
        public override void Dispose()
        {
            StopPolling();
 
            try
            {
                _pairedDevicesLock.Enter();
                foreach (var pairedDevice in _pairedDevices.Values)
                {
                    if (pairedDevice is IDisposable)
                        ((IDisposable)pairedDevice).Dispose();
                }
                _pairedDevices.Clear();
            }
            finally
            {
                _pairedDevicesLock.Leave();
            }
 
            _deviceFactory.Dispose();
            base.Dispose();
        }
 
        #endregion
 
        #region Private Members
 
        private void SendServerStatusRequest()
        {
            var command = new CommandSet(
                LyrionConstants.ServerStatusCommand,
                LyrionConstants.ServerStatusCommand,
                CommonCommandGroupType.Other,
                null,
                false,
                CommandPriority.Normal,
                StandardCommandsEnum.NotAStandardCommand);
 
            SendCommand(command);
        }
 
        private void SendSubscribeCommand()
        {
            var command = new CommandSet(
                LyrionConstants.SubscribeCommand,
                LyrionConstants.SubscribeCommand,
                CommonCommandGroupType.Other,
                null,
                false,
                CommandPriority.Normal,
                StandardCommandsEnum.NotAStandardCommand);
 
            SendCommand(command);
        }
 
        private void RequestPlayerStatus(string playerId)
        {
            var encodedId = LyrionResponseParser.UrlEncode(playerId);
            var commandStr = string.Format("{0} {1}", encodedId, LyrionConstants.PlayerStatusCommand);
 
            var command = new CommandSet(
                commandStr,
                commandStr,
                CommonCommandGroupType.Other,
                null,
                false,
                CommandPriority.Normal,
                StandardCommandsEnum.NotAStandardCommand);
 
            SendCommand(command);
        }
 
        private void StartPolling()
        {
            StopPolling();
            _pollTimer = new CTimer(PollTimerCallback, null, PollIntervalMs, PollIntervalMs);
        }
 
        private void StopPolling()
        {
            if (_pollTimer != null)
            {
                _pollTimer.Stop();
                _pollTimer.Dispose();
                _pollTimer = null;
            }
        }
 
        private void PollTimerCallback(object userSpecific)
        {
            if (IsConnected)
            {
                SendServerStatusRequest();
            }
        }
 
        private void Factory_DeviceStatusChanged(object sender, LyrionDeviceFactoryStatusEventArgs args)
        {
            switch (args.Status)
            {
                case DeviceStatus.Added:
                    AddLyrionPairedDevice(args.Device);
                    break;
                case DeviceStatus.Updated:
                    UpdateLyrionPairedDevice(args.Device);
                    break;
                case DeviceStatus.Removed:
                    RemoveLyrionPairedDevice(args.Device);
                    break;
            }
        }
 
        private void AddLyrionPairedDevice(IPairedDevice pairedDevice)
        {
            pairedDevice.SetConnectionStatus(IsConnected);
            AddPairedDevice(pairedDevice.PairedDeviceInformation, pairedDevice as ABasicDriver);
 
            try
            {
                _pairedDevicesLock.Enter();
                _pairedDevices[pairedDevice.PairedDeviceInformation.Id] = pairedDevice;
            }
            finally
            {
                _pairedDevicesLock.Leave();
            }
 
            // Request full status for the newly added player
            RequestPlayerStatus(pairedDevice.PairedDeviceInformation.Id);
        }
 
        private void UpdateLyrionPairedDevice(IPairedDevice updatedDevice)
        {
            updatedDevice.SetConnectionStatus(IsConnected);
            UpdatePairedDevice(updatedDevice.PairedDeviceInformation.Id,
                updatedDevice.PairedDeviceInformation);
 
            IPairedDevice oldDevice = null;
            bool needsDisposal = false;
 
            try
            {
                _pairedDevicesLock.Enter();
                if (_pairedDevices.TryGetValue(updatedDevice.PairedDeviceInformation.Id, out oldDevice))
                {
                    if (oldDevice == updatedDevice)
                        return;
 
                    _pairedDevices[updatedDevice.PairedDeviceInformation.Id] = updatedDevice;
                    needsDisposal = true;
                }
            }
            finally
            {
                _pairedDevicesLock.Leave();
            }
 
            if (needsDisposal && oldDevice is IDisposable)
                ((IDisposable)oldDevice).Dispose();
        }
 
        private void RemoveLyrionPairedDevice(IPairedDevice pairedDevice)
        {
            try
            {
                _pairedDevicesLock.Enter();
                if (_pairedDevices.ContainsKey(pairedDevice.PairedDeviceInformation.Id))
                {
                    RemovePairedDevice(pairedDevice.PairedDeviceInformation.Id);
                    _pairedDevices.Remove(pairedDevice.PairedDeviceInformation.Id);
                }
            }
            finally
            {
                _pairedDevicesLock.Leave();
            }
 
            if (pairedDevice is IDisposable)
                ((IDisposable)pairedDevice).Dispose();
        }
 
        private ValidatedRxData LyrionValidateResponse(string response, CommonCommandGroupType commandGroup)
        {
            // All responses from LMS CLI are valid text lines
            if (!string.IsNullOrEmpty(response))
                return new ValidatedRxData(true, response);
 
            return new ValidatedRxData(false, string.Empty);
        }
 
        #endregion
    }
}