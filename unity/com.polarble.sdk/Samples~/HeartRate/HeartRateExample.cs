using UnityEngine;
using PolarBle.Unity;

public sealed class HeartRateExample : MonoBehaviour
{
    [SerializeField] private PolarUnityClient client;
    [SerializeField] private string deviceId;
    private async void Start()
    {
        client.DeviceFound += device => Debug.Log($"{device.Name}  {device.Id}  RSSI {device.Rssi}");
        client.HeartRate += sample => Debug.Log($"HR {sample.BeatsPerMinute} bpm");
        client.Error += error => Debug.LogException(error);
        try { if (string.IsNullOrEmpty(deviceId)) await client.ScanAsync(); else await client.ConnectAsync(deviceId); }
        catch (System.Exception error) { Debug.LogException(error); }
    }
}
