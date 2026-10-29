using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PolarBle.Transport
{
    public sealed class BleTransportError : EventArgs
    {
        public string Operation { get; }
        public Exception Exception { get; }
        public BleTransportError(string operation, Exception exception) { Operation = operation; Exception = exception; }
    }
    public sealed class BleDevice : EventArgs
    {
        public string Id { get; }
        public string Name { get; }
        public ulong Address { get; }
        public short Rssi { get; }
        public IReadOnlyDictionary<ushort, byte[]> ManufacturerData { get; }
        public IReadOnlyList<Guid> ServiceUuids { get; }
        public BleDevice(string id, string name, ulong address, short rssi,
            IReadOnlyDictionary<ushort, byte[]>? manufacturerData = null, IReadOnlyList<Guid>? serviceUuids = null)
        {
            Id = id; Name = name; Address = address; Rssi = rssi;
            ManufacturerData = manufacturerData ?? new Dictionary<ushort, byte[]>();
            ServiceUuids = serviceUuids ?? Array.Empty<Guid>();
        }
    }

    public sealed class BleNotification : EventArgs
    {
        public Guid ServiceUuid { get; }
        public Guid CharacteristicUuid { get; }
        public byte[] Data { get; }
        public BleNotification(Guid serviceUuid, Guid characteristicUuid, byte[] data)
        { ServiceUuid = serviceUuid; CharacteristicUuid = characteristicUuid; Data = data; }
    }

    [Flags]
    public enum BleCharacteristicProperties { None = 0, Read = 1, Write = 2, WriteWithoutResponse = 4, Notify = 8, Indicate = 16 }

    public sealed class BleGattCharacteristic
    {
        public Guid Uuid { get; }
        public BleCharacteristicProperties Properties { get; }
        public BleGattCharacteristic(Guid uuid, BleCharacteristicProperties properties) { Uuid = uuid; Properties = properties; }
    }

    public sealed class BleGattService
    {
        public Guid Uuid { get; }
        public IReadOnlyList<BleGattCharacteristic> Characteristics { get; }
        public BleGattService(Guid uuid, IReadOnlyList<BleGattCharacteristic> characteristics) { Uuid = uuid; Characteristics = characteristics; }
    }

    /// <summary>OS BLE boundary. All payloads are GATT characteristic values, without ATT headers.</summary>
    public interface IBleTransport : IDisposable
    {
        event EventHandler<BleDevice>? DeviceDiscovered;
        event EventHandler<BleTransportError>? ErrorOccurred;
        Task StartScanAsync(CancellationToken cancellationToken = default);
        Task StopScanAsync(CancellationToken cancellationToken = default);
        /// <param name="deviceId">An identifier returned by DeviceDiscovered (not an assumed Polar serial).</param>
        Task<IBleConnection> ConnectAsync(string deviceId, CancellationToken cancellationToken = default);
    }

    /// <summary>Optional platform pairing capability. Never removes an existing bond.</summary>
    public interface IPairingBleTransport
    {
        Task PairAsync(string deviceId, CancellationToken cancellationToken = default);
    }

    public interface IBleConnection : IDisposable
    {
        string DeviceId { get; }
        bool IsConnected { get; }
        /// <summary>Maximum characteristic value payload: negotiated ATT MTU minus three bytes.</summary>
        int MaxWriteSize { get; }
        event EventHandler<BleNotification>? NotificationReceived;
        event EventHandler? Disconnected;
        event EventHandler<BleTransportError>? ErrorOccurred;
        Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(CancellationToken cancellationToken = default);
        Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default);
        Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data, bool withResponse, CancellationToken cancellationToken = default);
        /// <summary>Subscribe to notifications or indications, choosing a supported CCCD mode.</summary>
        Task SetNotificationsAsync(Guid serviceUuid, Guid characteristicUuid, bool enabled, CancellationToken cancellationToken = default);
        Task DisconnectAsync(CancellationToken cancellationToken = default);
    }
}
