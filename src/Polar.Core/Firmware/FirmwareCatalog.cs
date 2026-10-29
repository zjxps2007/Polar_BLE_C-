// Firmware Management protocol ported from Polar BLE SDK. See LICENSES/Polar_SDK_License.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using PolarBle.Protocol.Protobuf;
using PolarBle.Services;

namespace PolarBle.Firmware
{
    public sealed class FirmwareInfo
    {
        public string Version { get; }
        public Uri DownloadUri { get; }
        public bool? Mandatory { get; }
        public FirmwareInfo(string version, Uri downloadUri, bool? mandatory) { Version = version; DownloadUri = downloadUri; Mandatory = mandatory; }
    }
    public sealed class FirmwareFile
    {
        public string Path { get; }
        public byte[] Data { get; }
        public FirmwareFile(string path, byte[] data) { Path = path; Data = data; }
    }
    /// <summary>Does not own HttpClient. Caller supplies its own truthful client identifier.</summary>
    public sealed class FirmwareCatalog
    {
        private readonly HttpClient _http;
        private readonly string _clientId;
        public FirmwareCatalog(HttpClient http, string clientId)
        { _http = http ?? throw new ArgumentNullException(nameof(http)); _clientId = string.IsNullOrWhiteSpace(clientId) ? throw new ArgumentException("clientId required.") : clientId; }
        public async Task<FirmwareInfo?> CheckAsync(PolarFileService files, string polarDeviceId, CancellationToken cancellationToken = default)
        {
            var device = await files.ReadMessageAsync("/DEVICE.BPB", PbDeviceInfo.Parser, cancellationToken).ConfigureAwait(false);
            if (device.DeviceVersion == null || string.IsNullOrWhiteSpace(device.HardwareCode)) throw new PolarProtocolException("Device firmware identity is incomplete.");
            string current = VersionString(device);
            var body = new Struct(); body.Fields.Add("clientId", Value.ForString(_clientId)); body.Fields.Add("uuid", Value.ForString(DeviceUuid(polarDeviceId)));
            body.Fields.Add("firmwareVersion", Value.ForString(current)); body.Fields.Add("hardwareCode", Value.ForString(device.HardwareCode));
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://firmware-management.polar.com/api/v1/firmware-update/check");
            request.Headers.Accept.ParseAdd("application/json"); request.Content = new StringContent(JsonFormatter.Default.Format(body), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            response.EnsureSuccessStatusCode();
            var json = JsonParser.Default.Parse<Struct>(Encoding.UTF8.GetString(await ReadBoundedAsync(await response.Content.ReadAsStreamAsync().ConfigureAwait(false), 1024 * 1024, cancellationToken).ConfigureAwait(false)));
            string version = RequiredString(json, "version");
            if (!IsNewer(current, version)) return null;
            var uri = new Uri(RequiredString(json, "fileUrl"), UriKind.Absolute);
            if (uri.Scheme != Uri.UriSchemeHttps) throw new PolarProtocolException("Firmware server returned a non-HTTPS download URI.");
            bool? mandatory = null;
            if (json.Fields.TryGetValue("hardwareVersions", out var hardware) && hardware.KindCase == Value.KindOneofCase.ListValue)
                foreach (var item in hardware.ListValue.Values)
                    if (item.KindCase == Value.KindOneofCase.StructValue && item.StructValue.Fields.TryGetValue("erpNumber", out var erp) && erp.StringValue == device.HardwareCode && item.StructValue.Fields.TryGetValue("mandatory", out var flag) && flag.KindCase == Value.KindOneofCase.BoolValue) mandatory = flag.BoolValue;
            return new FirmwareInfo(version, uri, mandatory);
        }
        public async Task<IReadOnlyList<FirmwareFile>> DownloadAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Firmware downloads require HTTPS; use ParsePackage for local files.", nameof(uri));
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return ParsePackage(await ReadBoundedAsync(stream, 128 * 1024 * 1024, cancellationToken).ConfigureAwait(false));
        }
        public static IReadOnlyList<FirmwareFile> ParsePackage(byte[] zip)
        {
            using var input = new MemoryStream(zip, false); using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            if (archive.Entries.Count > 128) throw new InvalidDataException("Too many firmware archive entries.");
            var output = new List<FirmwareFile>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                if (entry.FullName.Equals("readme.txt", StringComparison.OrdinalIgnoreCase)) continue;
                string name = entry.FullName;
                if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains("\\") || name.Contains(":") || name.Split('/').Any(x => x == ".." || x == "." || x.Length == 0) || !names.Add(name))
                    throw new InvalidDataException("Invalid firmware entry path: " + name);
                total = checked(total + entry.Length); if (total > 256 * 1024 * 1024) throw new InvalidDataException("Firmware archive is too large.");
                using var data = entry.Open(); using var buffer = new MemoryStream(); var scratch = new byte[8192]; int read;
                while ((read = data.Read(scratch, 0, scratch.Length)) != 0)
                { if (buffer.Length + read > entry.Length) throw new InvalidDataException("Archive size mismatch."); buffer.Write(scratch, 0, read); }
                if (buffer.Length != entry.Length) throw new InvalidDataException("Truncated archive entry.");
                output.Add(new FirmwareFile("/" + name, buffer.ToArray()));
            }
            if (output.Count == 0) throw new InvalidDataException("Firmware archive contains no files.");
            return output.OrderBy(x => x.Path.EndsWith("SYSUPDAT.IMG", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToArray();
        }
        public static string DeviceUuid(string polarDeviceId)
        {
            if (polarDeviceId == null || polarDeviceId.Length != 8 || polarDeviceId.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Polar device ID must contain eight hexadecimal characters.", nameof(polarDeviceId));
            return "0e030000-0084-0000-0000-0000" + polarDeviceId.ToLowerInvariant();
        }
        public static bool IsNewer(string current, string available) => CompareVersions(available, current) > 0;
        public static int CompareVersions(string left, string right)
        {
            var a = ParseVersion(left); var b = ParseVersion(right);
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++) { int c = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0); if (c != 0) return c; }
            return 0;
        }
        private static uint[] ParseVersion(string value)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("A numeric firmware version is required.", nameof(value));
            var parts = value.Split('.'); var numbers = new uint[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!uint.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
                    throw new ArgumentException("Firmware version must contain unsigned decimal components separated by periods.", nameof(value));
            return numbers;
        }
        internal static string VersionString(PbDeviceInfo data)
        {
            if (data.DeviceVersion == null) throw new PolarProtocolException("Device firmware version is missing.");
            return $"{data.DeviceVersion.Major}.{data.DeviceVersion.Minor}.{data.DeviceVersion.Patch}";
        }
        private static string RequiredString(Struct value, string name)
        { if (!value.Fields.TryGetValue(name, out var item) || item.KindCase != Value.KindOneofCase.StringValue || item.StringValue.Length == 0) throw new PolarProtocolException("Firmware response missing " + name); return item.StringValue; }
        private static async Task<byte[]> ReadBoundedAsync(Stream input, int maximumBytes, CancellationToken token)
        {
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
            { if (output.Length + count > maximumBytes) throw new InvalidDataException("HTTP payload exceeds configured size limit."); output.Write(buffer, 0, count); }
            return output.ToArray();
        }
    }
}
