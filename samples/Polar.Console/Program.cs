using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PolarBle;
using PolarBle.Protocol.Pmd;
using PolarBle.Windows;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string command = args.Length == 0 ? "help" : args[0];
        if (command == "help" || !new[] { "scan", "inspect", "hr", "acc", "recordings", "pair" }.Contains(command))
        {
            Console.WriteLine("Polar.Console scan [seconds]\nPolar.Console inspect|hr|acc|recordings|pair <transport-device-id> [seconds]\nUse the transport ID from scan. Commands do not delete recordings or update firmware."); return command == "help" ? 0 : 2;
        }
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        using var client = new PolarClient(new WindowsBleTransport());
        client.ErrorOccurred += (_, error) => Console.Error.WriteLine($"{error.Operation}: {error.Exception.Message}");
        try
        {
            if (command == "scan")
            {
                var seen = new HashSet<string>();
                client.DeviceDiscovered += (_, device) => { lock (seen) if (seen.Add(device.Id)) Console.WriteLine($"{device.Name}\t{device.Id}\tRSSI {device.Rssi}"); };
                await client.StartScanAsync(cancel.Token);
                try { await Task.Delay(TimeSpan.FromSeconds(args.Length > 1 ? int.Parse(args[1]) : 10), cancel.Token); }
                finally { await client.StopScanAsync(CancellationToken.None); }
                return 0;
            }
            if (args.Length < 2) { Console.Error.WriteLine("A transport device ID is required. Run scan first."); return 2; }
            if (command == "pair") { await client.PairAsync(args[1], cancel.Token); Console.WriteLine("Pairing completed."); return 0; }
            var services = PolarServices.DeviceInformation | PolarServices.Battery | PolarServices.HeartRate;
            if (command == "acc") services |= PolarServices.Pmd;
            if (command == "recordings") services |= PolarServices.FileTransfer;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var session = await client.ConnectAsync(args[1], services, cancellationToken: timeout.Token);
            Console.WriteLine($"Connected: {session.DeviceId}; max GATT payload {session.MaxWriteSize}; battery {session.BatteryLevel?.ToString() ?? "unknown"}%");
            foreach (var item in session.DeviceInformation) Console.WriteLine($"DIS {item.Key}: {item.Value}");
            if (command == "inspect") return 0;
            if (command == "recordings")
            {
                await session.Device.RunSyncAsync(async token =>
                {
                    foreach (var entry in await session.Offline.ListAsync(cancellationToken: token)) Console.WriteLine(entry.Path);
                }, timeout.Token);
                return 0;
            }
            int frames = 0, samples = 0;
            if (command == "hr")
            {
                session.HeartRateReceived += (_, data) => { Interlocked.Increment(ref frames); Console.WriteLine($"HR {data.BeatsPerMinute}; RR(ms) {string.Join(",", data.RrIntervalsMilliseconds)}"); };
                await session.StartHeartRateAsync(timeout.Token);
            }
            else
            {
                var available = await session.Pmd.GetSettingsAsync(PmdMeasurementType.Accelerometer, cancellationToken: timeout.Token);
                var selected = available.SelectMaximum().Selected.ToDictionary(x => x.Key, x => x.Value);
                if (available.Available.TryGetValue(PmdSettingType.SampleRate, out var rates)) selected[PmdSettingType.SampleRate] = rates.Min();
                session.Pmd.DataReceived += (_, batch) => { if (batch.Type != PmdMeasurementType.Accelerometer) return; Interlocked.Increment(ref frames); Interlocked.Add(ref samples, batch.Samples.Count); };
                session.Pmd.ErrorOccurred += (_, error) => Console.Error.WriteLine(error.Error.Message);
                await session.Pmd.StartAsync(PmdMeasurementType.Accelerometer, new PmdSettings(selected), cancellationToken: timeout.Token);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[2]) : 10), cancel.Token); }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (session.IsConnected)
                    if (command == "hr") await session.StopHeartRateAsync(cleanup.Token);
                    else await session.Pmd.StopAsync(PmdMeasurementType.Accelerometer, cleanup.Token);
            }
            Console.WriteLine($"Received {frames} frames, {samples} PMD samples."); return frames > 0 ? 0 : 3;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return 130; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
