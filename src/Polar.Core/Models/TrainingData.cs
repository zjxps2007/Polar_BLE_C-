using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Google.Protobuf;
using PolarBle.Protocol.Protobuf;
using PolarBle.Services;

namespace PolarBle.Models
{
    public sealed class PolarExerciseEntry
    {
        public string Path { get; }
        public string Identifier { get; }
        /// <summary>Unknown for H10 files, which do not encode a recording date in their directory name.</summary>
        public DateTime? StartTime { get; }
        public ulong Size { get; }
        internal PolarExerciseEntry(PolarFileEntry file)
        {
            Path = file.Path; Size = file.Size; var parts = file.Path.Split('/');
            if (parts.Length >= 7 && parts[1] == "U" && parts[2] == "0" && parts[4] == "E" &&
                DateTime.TryParseExact(parts[3] + parts[5], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            { Identifier = parts[3] + parts[5]; StartTime = date; }
            else Identifier = parts.Length > 1 ? parts[1] : string.Empty;
        }
    }
    public sealed class PolarExerciseData
    {
        public TimeSpan RecordingInterval { get; }
        public bool IsRrInterval { get; }
        public IReadOnlyList<uint> Samples { get; }
        public PbExerciseSamples Source { get; }
        public PolarExerciseData(PbExerciseSamples source)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source)); RecordingInterval = PolarDataTime.Duration(source.RecordingInterval);
            IsRrInterval = source.RrSamples != null;
            Samples = source.RrSamples != null ? source.RrSamples.RrIntervals.ToArray() : source.HeartRateSamples.ToArray();
        }
    }
    public sealed class PolarRecordingStatus
    {
        public bool IsRecording { get; }
        public string? Identifier { get; }
        public PbRequestRecordingStatusResult Source { get; }
        internal PolarRecordingStatus(PbRequestRecordingStatusResult source)
        { Source = source; IsRecording = source.RecordingOn; Identifier = source.HasSampleDataIdentifier ? source.SampleDataIdentifier : null; }
    }
    public sealed class PolarExerciseStatus
    {
        public PbPftpGetExerciseStatusResult.Types.PbExerciseState State { get; }
        public PbPftpGetExerciseStatusResult.Types.PbExerciseType Type { get; }
        public ulong? SportIdentifier { get; }
        public PbPftpGetExerciseStatusResult Source { get; }
        internal PolarExerciseStatus(PbPftpGetExerciseStatusResult source)
        { Source = source; State = source.ExerciseState; Type = source.ExerciseType; SportIdentifier = source.SportIdentifier?.Value; }
    }
    public enum PolarOfflineExerciseStartState { Other = 0, Success = 1, ExerciseOngoing = 2, LowBattery = 3, SdkMode = 4, UnknownSport = 5 }
    public sealed class PolarOfflineExerciseStartResult
    {
        public PolarOfflineExerciseStartState Result { get; }
        public string? Directory { get; }
        public PbPftpStartDmExerciseResult Source { get; }
        internal PolarOfflineExerciseStartResult(PbPftpStartDmExerciseResult source)
        { Source = source; Result = (PolarOfflineExerciseStartState)(int)source.Result; Directory = source.HasDmDirectoryPath ? source.DmDirectoryPath : null; }
    }
    public enum PolarExerciseFileType { Summary, Samples, AdvancedSamples, Route, AdvancedRoute, Other }
    public sealed class PolarTrainingExerciseReference
    {
        public int Index { get; }
        public string Directory { get; }
        public IReadOnlyDictionary<string, ulong> FileSizes { get; }
        public IReadOnlyCollection<PolarExerciseFileType> FileTypes { get; }
        internal PolarTrainingExerciseReference(int index, string directory, IEnumerable<PolarFileEntry> files)
        {
            Index = index; Directory = directory; FileSizes = files.ToDictionary(x => x.Path, x => x.Size, StringComparer.Ordinal);
            FileTypes = FileSizes.Keys.Select(Kind).Distinct().ToArray();
        }
        internal static PolarExerciseFileType Kind(string path)
        {
            var name = path.Substring(path.LastIndexOf('/') + 1).Replace(".GZB", ".BPB");
            return name switch { "BASE.BPB" => PolarExerciseFileType.Summary, "SAMPLES.BPB" => PolarExerciseFileType.Samples, "SAMPLES2.BPB" => PolarExerciseFileType.AdvancedSamples,
                "ROUTE.BPB" => PolarExerciseFileType.Route, "ROUTE2.BPB" => PolarExerciseFileType.AdvancedRoute, _ => PolarExerciseFileType.Other };
        }
    }
    public sealed class PolarTrainingSessionReference
    {
        public string Directory { get; }
        public DateTime StartTime { get; }
        public ulong TotalBytes { get; }
        public IReadOnlyList<PolarTrainingExerciseReference> Exercises { get; }
        public IReadOnlyDictionary<string, ulong> FileSizes { get; }
        internal PolarTrainingSessionReference(string directory, IEnumerable<PolarFileEntry> files)
        {
            Directory = directory; var parts = directory.Split('/');
            StartTime = DateTime.ParseExact(parts[3] + parts[5], "yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var entries = files.Where(x => !x.IsDirectory).ToArray(); FileSizes = entries.ToDictionary(x => x.Path, x => x.Size, StringComparer.Ordinal);
            TotalBytes = entries.Aggregate(0UL, (sum, x) => checked(sum + x.Size));
            var exercises = new List<PolarTrainingExerciseReference>();
            foreach (var group in entries.Where(x => x.Path.StartsWith(directory, StringComparison.Ordinal)).GroupBy(x => x.Path.Substring(directory.Length).Split('/')[0]))
                if (int.TryParse(group.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    exercises.Add(new PolarTrainingExerciseReference(index, directory + group.Key + "/", group));
            Exercises = exercises.OrderBy(x => x.Index).ToArray();
        }
    }
    public sealed class PolarTrainingExercise
    {
        public int Index { get; }
        public string Directory { get; }
        public PbExerciseBase? Summary { get; }
        public PbExerciseSamples? Samples { get; }
        public PbExerciseSamples2? AdvancedSamples { get; }
        public PbExerciseRouteSamples? Route { get; }
        public PbExerciseRouteSamples2? AdvancedRoute { get; }
        public IReadOnlyDictionary<string, IMessage> SourceFiles { get; }
        internal PolarTrainingExercise(int index, string directory, IReadOnlyDictionary<string, IMessage> files)
        {
            Index = index; Directory = directory; SourceFiles = files;
            Summary = Single<PbExerciseBase>(files); Samples = Single<PbExerciseSamples>(files); AdvancedSamples = Single<PbExerciseSamples2>(files);
            Route = Single<PbExerciseRouteSamples>(files); AdvancedRoute = Single<PbExerciseRouteSamples2>(files);
        }
        // Devices normally store one format of each file. Preserve every source if both compressed/uncompressed copies exist.
        private static T? Single<T>(IReadOnlyDictionary<string, IMessage> files) where T : class, IMessage => files.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).OfType<T>().FirstOrDefault();
    }
    public sealed class PolarTrainingProgress
    {
        public ulong TotalBytes { get; }
        public ulong CompletedBytes { get; }
        public string CurrentFile { get; }
        public double Percent => TotalBytes == 0 ? 100 : Math.Min(100, CompletedBytes * 100.0 / TotalBytes);
        public PolarTrainingProgress(ulong total, ulong completed, string currentFile) { TotalBytes = total; CompletedBytes = completed; CurrentFile = currentFile; }
    }
}
