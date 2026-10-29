using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Transport;
using PolarBle.Unity;
using UnityEngine;

namespace PolarValidation
{
    [Serializable]
    public sealed class SmokeResult
    {
        public string utc, unityVersion, platform, backend, radioState;
        public bool success, nativeLoaded, scanStarted, scanStopped, connected, disconnected;
        public int advertisements, polarDevices, services, maxWriteSize;
        public string firmwareRevision, softwareRevision;
        public string[] errors;
    }

    public static class PolarSmoke
    {
        public static string Argument(string name, string fallback = null)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : fallback;
        }

        public static async Task<SmokeResult> RunAsync(string backend, CancellationToken token)
        {
            var result = new SmokeResult { utc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(), backend = backend };
            var errors = new ConcurrentQueue<string>();
            var polar = new ConcurrentDictionary<string, byte>();
            int advertisements = 0;
            try
            {
                using (var transport = new NativeWindowsBleTransport())
                {
                    result.nativeLoaded = true;
                    transport.ErrorOccurred += (_, error) => errors.Enqueue(error.Operation + ": " + error.Exception);
                    result.radioState = (await transport.GetRadioStateAsync(token)).ToString();
                    transport.DeviceDiscovered += (_, device) =>
                    {
                        Interlocked.Increment(ref advertisements);
                        if (device.Name.StartsWith("Polar", StringComparison.OrdinalIgnoreCase)) polar.TryAdd(device.Id, 0);
                    };
                    await transport.StartScanAsync(token);
                    result.scanStarted = true;
                    await Task.Delay(3000, token);
                    await transport.StopScanAsync(token);
                    result.scanStopped = true;
                    string deviceId = Argument("--polar-device");
                    if (!string.IsNullOrWhiteSpace(deviceId))
                    {
                        using (var connection = await transport.ConnectAsync(deviceId, token))
                        {
                            result.connected = connection.IsConnected;
                            result.maxWriteSize = connection.MaxWriteSize;
                            result.services = (await connection.DiscoverServicesAsync(token)).Count;
                            var dis = new Guid("0000180a-0000-1000-8000-00805f9b34fb");
                            result.firmwareRevision = Encoding.UTF8.GetString(await connection.ReadAsync(dis, new Guid("00002a26-0000-1000-8000-00805f9b34fb"), token));
                            result.softwareRevision = Encoding.UTF8.GetString(await connection.ReadAsync(dis, new Guid("00002a28-0000-1000-8000-00805f9b34fb"), token));
                            await connection.DisconnectAsync(token);
                            result.disconnected = !connection.IsConnected;
                        }
                    }
                }
            }
            catch (Exception exception) { errors.Enqueue(exception.ToString()); }
            result.advertisements = advertisements;
            result.polarDevices = polar.Count;
            result.errors = errors.ToArray();
            result.success = result.nativeLoaded && result.scanStarted && result.scanStopped && result.errors.Length == 0;
            if (!string.IsNullOrWhiteSpace(Argument("--polar-device")))
                result.success &= result.connected && result.disconnected && result.services > 0 && !string.IsNullOrEmpty(result.softwareRevision);
            return result;
        }

        public static void Write(SmokeResult result, string fileName)
        {
            string directory = Argument("--polar-results", Path.Combine(Application.dataPath, "../SmokeResults"));
            Directory.CreateDirectory(directory);
            string json = JsonUtility.ToJson(result, true);
            File.WriteAllText(Path.Combine(directory, fileName), json, new UTF8Encoding(false));
            Debug.Log("POLAR_SMOKE " + json);
        }
    }

    public sealed class PolarSmokeRunner : MonoBehaviour
    {
        private CancellationTokenSource _lifetime;
        private async void Start()
        {
            _lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
#if ENABLE_IL2CPP
            const string backend = "WindowsStandalone-IL2CPP";
#else
            const string backend = "WindowsStandalone-Mono";
#endif
            var result = await PolarSmoke.RunAsync(backend, _lifetime.Token);
            PolarSmoke.Write(result, backend + ".json");
            Application.Quit(result.success ? 0 : 1);
        }
        private void OnDestroy() { _lifetime?.Cancel(); _lifetime?.Dispose(); }
    }
}
