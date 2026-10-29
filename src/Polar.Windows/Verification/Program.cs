using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PolarBle;
using PolarBle.Native;
using PolarBle.Transport;
using PolarBle.Windows;
using PolarBle.Protocol.HeartRate;
using PolarBle.Protocol.Pmd;
using PolarBle.Unity;

internal static class Program
{
    private static readonly Guid Service = Guid.Parse("fb005c80-02e7-f387-1cad-8acd2d8df0c8");
    private static readonly Guid Characteristic = Guid.Parse("fb005c81-02e7-f387-1cad-8acd2d8df0c8");
    private static int _assertions;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            await VerifyWireAndQueue();
            Console.WriteLine("PASS native bridge wire/queue: " + _assertions + " assertions; Unity adapter compiled.");
            if (Array.IndexOf(args, "--scan") >= 0) await ScanWindows(Array.IndexOf(args, "--native") >= 0);
            int deviceIndex = Array.IndexOf(args, "--device");
            if (deviceIndex >= 0 && deviceIndex + 1 < args.Length)
            {
                if (Array.IndexOf(args, "--diagnose-windows") >= 0)
                {
                    await DiagnoseWindows(args[deviceIndex + 1]);
                    return 0;
                }
                int secondsIndex = Array.IndexOf(args, "--stream-seconds");
                int seconds = secondsIndex >= 0 && secondsIndex + 1 < args.Length ? int.Parse(args[secondsIndex + 1]) : 10;
                using IBleTransport transport = Array.IndexOf(args, "--native") >= 0 ? new NativeWindowsBleTransport() : new WindowsBleTransport();
                if (Array.IndexOf(args, "--pair") >= 0)
                {
                    Console.WriteLine("Pairing with Windows: " + args[deviceIndex + 1]);
                    using var pairTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await ((IPairingBleTransport)transport).PairAsync(args[deviceIndex + 1], pairTimeout.Token);
                    Console.WriteLine("Windows pairing completed or already paired.");
                }
                await StreamDevice(transport, args[deviceIndex + 1], seconds, Array.IndexOf(args, "--hr-only") >= 0,
                    Array.IndexOf(args, "--inspect-only") >= 0);
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task DiagnoseWindows(string id)
    {
        ulong address = ulong.Parse(id.Substring(4), System.Globalization.NumberStyles.HexNumber);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var selector = global::Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromBluetoothAddress(address);
        var endpoints = await global::Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector,
            new[] { "System.Devices.Aep.IsPaired", "System.Devices.Aep.IsConnected", "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.Bluetooth.Le.AddressType" },
            global::Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint).AsTask(deadline.Token).WaitAsync(deadline.Token);
        Console.WriteLine("Windows AEP count=" + endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            Console.WriteLine("AEP=" + endpoint.Id + " name=" + endpoint.Name + " paired=" + endpoint.Pairing.IsPaired + " canPair=" + endpoint.Pairing.CanPair
                + " protection=" + endpoint.Pairing.ProtectionLevel);
            foreach (var property in endpoint.Properties) Console.WriteLine("Property " + property.Key + "=" + property.Value);
            using var device = await global::Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(endpoint.Id).AsTask(deadline.Token).WaitAsync(deadline.Token);
            if (device == null) { Console.WriteLine("FromId=null"); continue; }
            Console.WriteLine("FromId address=" + device.BluetoothAddress.ToString("X12") + " type=" + device.BluetoothAddressType + " status=" + device.ConnectionStatus);
            var services = await device.GetGattServicesAsync(global::Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached).AsTask(deadline.Token).WaitAsync(deadline.Token);
            Console.WriteLine("FromId uncached discovery=" + services.Status + " protocol=" + services.ProtocolError + " services=" + services.Services.Count + " connection=" + device.ConnectionStatus);
            foreach (var service in services.Services) service.Dispose();
            var cached = await device.GetGattServicesAsync(global::Windows.Devices.Bluetooth.BluetoothCacheMode.Cached).AsTask(deadline.Token).WaitAsync(deadline.Token);
            Console.WriteLine("FromId cached discovery=" + cached.Status + " protocol=" + cached.ProtocolError + " services=" + cached.Services.Count);
            foreach (var service in cached.Services) service.Dispose();
            foreach (var uuid in new[] { Guid.Parse("0000180d-0000-1000-8000-00805f9b34fb"), Service })
            {
                var targeted = await device.GetGattServicesForUuidAsync(uuid, global::Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached)
                    .AsTask(deadline.Token).WaitAsync(deadline.Token);
                Console.WriteLine("FromId targeted " + uuid + " discovery=" + targeted.Status + " protocol=" + targeted.ProtocolError + " services=" + targeted.Services.Count);
                foreach (var service in targeted.Services) service.Dispose();
            }
        }
    }

    private static async Task VerifyWireAndQueue()
    {
        byte[] encoded = Wire.Encode(w => { Wire.Text(w, "Polar 한글 ⚡"); w.Write(Service.ToByteArray()); Wire.Bytes(w, new byte[] { 0, 128, 255 }); });
        using (var reader = new BinaryReader(new MemoryStream(encoded), Wire.Utf8))
        {
            Check(Wire.Text(reader) == "Polar 한글 ⚡", "UTF-8 text");
            Check(Wire.Guid(reader) == Service, "GUID ordering");
            Check(Convert.ToHexString(Wire.Bytes(reader)) == "0080FF", "binary buffer");
            Wire.End(reader);
        }
        bool rejected = false;
        try { using var reader = new BinaryReader(new MemoryStream(new byte[] { 255, 255, 255, 127 })); Wire.Bytes(reader); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "malformed length rejected");

        var fake = new FakeTransport();
        using var context = new NativeContext(fake);
        context.Submit(1, 1, 0, Array.Empty<byte>());
        Check((await Next(context)).Kind == 1 && fake.Scanning, "scan completion");
        fake.EmitDevice();
        var found = await Next(context);
        Check(found.Kind == 3, "advertisement event");
        using (var reader = new BinaryReader(new MemoryStream(found.Payload), Wire.Utf8))
        { Check(Wire.Text(reader) == "ble:010203040506", "advertisement device ID"); Check(Wire.Text(reader) == "Polar Test 한글", "advertisement name"); }
        context.Submit(2, 3, 0, Wire.Encode(w => Wire.Text(w, "ble:010203040506")));
        var connected = await Next(context); long connection = connected.ConnectionId;
        Check(connected.Kind == 1 && connection > 0, "connect completion");
        Check(context.MaxWriteSize(connection) == 229, "MTU payload query");
        context.Submit(3, 4, connection, Array.Empty<byte>());
        var discovery = await Next(context);
        using (var reader = new BinaryReader(new MemoryStream(discovery.Payload)))
        {
            Check(reader.ReadInt32() == 229 && reader.ReadInt32() == 1, "service count");
            Check(Wire.Guid(reader) == Service && reader.ReadInt32() == 1, "service UUID");
            Check(Wire.Guid(reader) == Characteristic && reader.ReadInt32() == 31, "characteristic UUID and property flags");
            Wire.End(reader);
        }
        context.Submit(4, 5, connection, Wire.Encode(w => { w.Write(Service.ToByteArray()); w.Write(Characteristic.ToByteArray()); }));
        Check(Convert.ToHexString((await Next(context)).Payload) == "112233", "read payload");
        context.Submit(5, 6, connection, Wire.Encode(w => { w.Write(Service.ToByteArray()); w.Write(Characteristic.ToByteArray()); w.Write((byte)0); Wire.Bytes(w, new byte[] { 4, 5, 6 }); }));
        Check((await Next(context)).Kind == 1 && Convert.ToHexString(fake.Connection.LastWrite) == "040506", "write payload");
        context.Submit(6, 7, connection, Wire.Encode(w => { w.Write(Service.ToByteArray()); w.Write(Characteristic.ToByteArray()); w.Write((byte)1); }));
        Check((await Next(context)).Kind == 1 && fake.Connection.Notifications, "CCCD subscription");
        fake.Connection.EmitNotification();
        var notification = await Next(context);
        Check(notification.Kind == 4 && notification.ConnectionId == connection, "notification routing");
        Check(ReferenceEquals(context.Peek(), context.Peek()), "empty queue peek is stable");
        fake.EmitError();
        Check((await Next(context)).Kind == 6, "background error propagated");
        context.Submit(7, 5, 999999, Wire.Encode(w => { w.Write(Service.ToByteArray()); w.Write(Characteristic.ToByteArray()); }));
        Check((await Next(context)).Kind == 2, "invalid handle becomes request error");
        fake.Connection.DelayRead = true;
        context.Submit(8, 5, connection, Wire.Encode(w => { w.Write(Service.ToByteArray()); w.Write(Characteristic.ToByteArray()); }));
        Check(context.Cancel(8), "cancel accepted");
        var canceled = await Next(context);
        using (var reader = new BinaryReader(new MemoryStream(canceled.Payload)))
            Check(canceled.Kind == 2 && reader.ReadInt32() == 1, "cancel completion category");
        context.Submit(9, 8, connection, Array.Empty<byte>());
        var first = await Next(context); var second = await Next(context);
        Check(first.Kind == 5 && second.Kind == 1, "disconnect lifecycle before completion");
        context.Submit(10, 2, 0, Array.Empty<byte>());
        Check((await Next(context)).Kind == 1 && !fake.Scanning, "stop scan completion");
        for (int i = 0; i < 33000; ++i) fake.EmitDevice();
        Check((await Next(context)).Kind == 6, "queue overflow explicit and prioritized");
        context.Dispose();
        Check(fake.Disposed, "context owns transport lifetime");

        using var radioTransport = new FakeTransport();
        using var radioContext = new NativeContext(radioTransport);
        radioContext.Submit(1, 10, 0, Array.Empty<byte>());
        var radioReply = await Next(radioContext);
        using (var reader = new BinaryReader(new MemoryStream(radioReply.Payload)))
            Check(radioReply.Kind == 1 && reader.ReadInt32() == (int)BleRadioState.On, "radio state query crosses native wire");
        radioTransport.EmitRadioState(BleRadioState.Disabled);
        var radioEvent = await Next(radioContext);
        using (var reader = new BinaryReader(new MemoryStream(radioEvent.Payload)))
            Check(radioEvent.Kind == 8 && reader.ReadInt32() == (int)BleRadioState.Disabled, "radio state event crosses native wire");

        using var failedTransport = new FakeTransport();
        failedTransport.Connection.DropWithoutNotification();
        using var failedContext = new NativeContext(failedTransport);
        failedContext.Submit(1, 3, 0, Wire.Encode(w => Wire.Text(w, "ble:010203040506")));
        var dropped = await Next(failedContext);
        var failed = await Next(failedContext);
        Check(dropped.Kind == 5 && failed.Kind == 2 && dropped.ConnectionId == failed.ConnectionId && failed.ConnectionId > 0,
            "connect loss before publication orders disconnect before failure for the same handle");
        failedTransport.Connection.EmitDelayedDisconnect();
        Check(failedContext.Peek() == null, "delayed disconnect cannot recreate a failed-connect marker");
    }

    private static async Task<NativeEvent> Next(NativeContext context)
    {
        for (int i = 0; i < 1000; ++i)
        {
            var item = context.Peek();
            if (item != null)
            {
                Check(ReferenceEquals(context.Peek(), item), "peek retains event until copy");
                context.Dequeue(item); return item;
            }
            await Task.Delay(5);
        }
        throw new TimeoutException("Native bridge failed to complete within five seconds.");
    }

    private static async Task ScanWindows(bool native)
    {
        using IBleTransport transport = native ? new NativeWindowsBleTransport() : new WindowsBleTransport();
        Console.WriteLine("Bluetooth radio=" + await ((IBleRadioStateProvider)transport).GetRadioStateAsync());
        int advertisements = 0;
        Exception? background = null;
        var polarDevices = new ConcurrentDictionary<string, byte>();
        transport.DeviceDiscovered += (_, device) =>
        {
            Interlocked.Increment(ref advertisements);
            if (device.Name.StartsWith("Polar", StringComparison.OrdinalIgnoreCase) && polarDevices.TryAdd(device.Id, 0))
                Console.WriteLine("Polar advertisement: " + device.Name + " id=" + device.Id + " RSSI=" + device.Rssi);
        };
        transport.ErrorOccurred += (_, error) => background = error.Exception;
        await transport.StartScanAsync();
        await Task.Delay(5000);
        await transport.StopScanAsync();
        await Task.Delay(100);
        if (background != null) throw new InvalidOperationException("Windows BLE scan failed.", background);
        Console.WriteLine("PASS " + transport.GetType().Name + " scan/start/stop; advertisements=" + advertisements + "; no sensor connected.");
    }

    private static async Task StreamDevice(IBleTransport transport, string deviceId, int seconds, bool hrOnly, bool inspectOnly)
    {
        if (seconds < 1 || seconds > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 60));
        var errors = new ConcurrentQueue<Exception>();
        transport.ErrorOccurred += (_, error) => errors.Enqueue(error.Exception);
        Console.WriteLine("Connecting via " + transport.GetType().Name + " to " + deviceId);
        using var connection = await transport.ConnectAsync(deviceId, deadline.Token);
        var services = await connection.DiscoverServicesAsync(deadline.Token);
        Console.WriteLine("Connected: maxWriteSize=" + connection.MaxWriteSize + "; GATT services=" + services.Count);
        foreach (var service in services)
            Console.WriteLine("GATT " + service.Uuid + " characteristics=" + string.Join(",", service.Characteristics.Select(c => c.Uuid + ":" + c.Properties)));
        var dis = Guid.Parse("0000180a-0000-1000-8000-00805f9b34fb");
        var firmware = Guid.Parse("00002a26-0000-1000-8000-00805f9b34fb");
        Console.WriteLine("Firmware=" + Encoding.UTF8.GetString(await connection.ReadAsync(dis, firmware, deadline.Token)));
        Console.WriteLine("SoftwareRevision=" + Encoding.UTF8.GetString(await connection.ReadAsync(dis, Guid.Parse("00002a28-0000-1000-8000-00805f9b34fb"), deadline.Token)));
        if (inspectOnly)
        {
            await connection.DisconnectAsync();
            if (connection.IsConnected) throw new InvalidOperationException("Disconnect did not finish.");
            Console.WriteLine("PASS live connect/discover/DIS-read/disconnect.");
            return;
        }
        int hrPackets = 0, lastHr = -1, accPackets = 0, accSamples = 0;
        connection.NotificationReceived += (_, notification) =>
        {
            if (notification.ServiceUuid == HeartRateParser.ServiceUuid && notification.CharacteristicUuid == HeartRateParser.MeasurementUuid)
            {
                try { lastHr = HeartRateParser.Parse(notification.Data).BeatsPerMinute; Interlocked.Increment(ref hrPackets); }
                catch (Exception ex) { errors.Enqueue(ex); }
            }
        };
        using var pmd = new PmdClient(connection);
        pmd.ErrorOccurred += (_, error) => errors.Enqueue(error.Error);
        pmd.DataReceived += (_, data) =>
        {
            if (data.Type == PmdMeasurementType.Accelerometer)
            { Interlocked.Increment(ref accPackets); Interlocked.Add(ref accSamples, data.Samples.Count); }
        };
        bool hrSubscribed = false, accStarted = false;
        try
        {
            await connection.SetNotificationsAsync(HeartRateParser.ServiceUuid, HeartRateParser.MeasurementUuid, true, deadline.Token);
            hrSubscribed = true;
            if (!hrOnly)
            {
                await pmd.InitializeAsync(deadline.Token);
            var features = await pmd.ReadFeaturesAsync(deadline.Token);
            Console.WriteLine("PMD features=" + string.Join(",", features));
            var status = await pmd.GetMeasurementStatusAsync(deadline.Token);
            if (status.TryGetValue(PmdMeasurementType.Accelerometer, out var accStatus) && accStatus != PmdMeasurementStatus.Inactive)
                throw new InvalidOperationException("ACC is already active; refusing to modify an existing recording/stream.");
            var available = await pmd.GetSettingsAsync(PmdMeasurementType.Accelerometer, cancellationToken: deadline.Token);
            Console.WriteLine("ACC available=" + string.Join(";", available.Available.Select(p => p.Key + "=" + string.Join(",", p.Value))));
            var selected = new PmdSettings(available.Available.Where(p => p.Value.Count > 0).ToDictionary(p => p.Key, p => p.Value.Min()));
            Console.WriteLine("ACC selected=" + string.Join(";", selected.Selected.Select(p => p.Key + "=" + p.Value)));
            await pmd.StartAsync(PmdMeasurementType.Accelerometer, selected, cancellationToken: deadline.Token);
            accStarted = true;
            }
            await Task.Delay(TimeSpan.FromSeconds(seconds), deadline.Token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (accStarted)
            {
                try { await pmd.StopAsync(PmdMeasurementType.Accelerometer, cleanup.Token); Console.WriteLine("ACC stop acknowledged."); }
                catch (Exception ex) { errors.Enqueue(ex); }
            }
            if (hrSubscribed)
            {
                try { await connection.SetNotificationsAsync(HeartRateParser.ServiceUuid, HeartRateParser.MeasurementUuid, false, cleanup.Token); }
                catch (Exception ex) { errors.Enqueue(ex); }
            }
            await connection.DisconnectAsync();
            Console.WriteLine("Disconnected: IsConnected=" + connection.IsConnected);
        }
        Console.WriteLine("Stream result: HR packets=" + hrPackets + " lastHR=" + lastHr + "; ACC packets=" + accPackets + " samples=" + accSamples + "; errors=" + errors.Count);
        if (!errors.IsEmpty) throw new AggregateException(errors);
        if (hrPackets == 0 || (!hrOnly && (accPackets == 0 || accSamples == 0))) throw new InvalidOperationException("Expected sensor data were not received.");
        Console.WriteLine("PASS live " + (hrOnly ? "HR" : "HR+ACC") + " stream, stop, disconnect.");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("FAIL: " + message); ++_assertions; }

    private sealed class FakeTransport : IBleTransport, IBleRadioStateProvider
    {
        internal readonly FakeConnection Connection = new FakeConnection();
        internal bool Scanning, Disposed;
        public event EventHandler<BleDevice>? DeviceDiscovered;
        public event EventHandler<BleTransportError>? ErrorOccurred;
        public event EventHandler<BleRadioStateChanged>? RadioStateChanged;
        public Task<BleRadioState> GetRadioStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(BleRadioState.On);
        internal void EmitRadioState(BleRadioState state) => RadioStateChanged?.Invoke(this, new BleRadioStateChanged(state));
        public Task StartScanAsync(CancellationToken cancellationToken = default) { Scanning = true; return Task.CompletedTask; }
        public Task StopScanAsync(CancellationToken cancellationToken = default) { Scanning = false; return Task.CompletedTask; }
        public Task<IBleConnection> ConnectAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult<IBleConnection>(Connection);
        internal void EmitDevice() => DeviceDiscovered?.Invoke(this, new BleDevice("ble:010203040506", "Polar Test 한글", 0x010203040506, -55,
            new Dictionary<ushort, byte[]> { [0x6B] = new byte[] { 1, 2, 3 } }, new[] { Service }));
        internal void EmitError() => ErrorOccurred?.Invoke(this, new BleTransportError("scan", new IOException("test failure")));
        public void Dispose() { Disposed = true; Connection.Dispose(); }
    }

    private sealed class FakeConnection : IBleConnection
    {
        public string DeviceId => "ble:010203040506";
        public bool IsConnected { get; private set; } = true;
        public int MaxWriteSize => 229;
        internal byte[] LastWrite = Array.Empty<byte>();
        internal bool Notifications, DelayRead;
        public event EventHandler<BleNotification>? NotificationReceived;
        public event EventHandler? Disconnected;
        public event EventHandler<BleTransportError>? ErrorOccurred { add { } remove { } }
        public Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BleGattService>>(new[] { new BleGattService(Service, new[] { new BleGattCharacteristic(Characteristic, (BleCharacteristicProperties)31) }) });
        public async Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default)
        { if (DelayRead) await Task.Delay(Timeout.Infinite, cancellationToken); return new byte[] { 0x11, 0x22, 0x33 }; }
        public Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data, bool withResponse, CancellationToken cancellationToken = default)
        { LastWrite = data; return Task.CompletedTask; }
        public Task SetNotificationsAsync(Guid serviceUuid, Guid characteristicUuid, bool enabled, CancellationToken cancellationToken = default)
        { Notifications = enabled; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { Dispose(); return Task.CompletedTask; }
        internal void EmitNotification() => NotificationReceived?.Invoke(this, new BleNotification(Service, Characteristic, new byte[] { 8, 9 }));
        internal void DropWithoutNotification() { IsConnected = false; }
        internal void EmitDelayedDisconnect() => Disconnected?.Invoke(this, EventArgs.Empty);
        public void Dispose() { if (IsConnected) { IsConnected = false; Disconnected?.Invoke(this, EventArgs.Empty); } }
    }
}
