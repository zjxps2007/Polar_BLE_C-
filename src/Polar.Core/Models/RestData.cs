using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf.WellKnownTypes;
using PolarBle.Services;

namespace PolarBle.Models
{
    public sealed class PolarRestServices
    {
        public IReadOnlyDictionary<string, string> Paths { get; }
        public Struct Source { get; }
        public PolarRestServices(Struct source) { Source = source; Paths = PolarRestDescription.StringMap(source, "services"); }
    }
    public sealed class PolarRestDescription
    {
        public IReadOnlyList<string> Events { get; }
        public IReadOnlyList<string> Endpoints { get; }
        public IReadOnlyDictionary<string, string> Actions { get; }
        public Struct Source { get; }
        public PolarRestDescription(Struct source)
        { Source = source ?? throw new ArgumentNullException(nameof(source)); Events = Strings(source, "events"); Endpoints = Strings(source, "endpoints"); Actions = StringMap(source, "cmd"); }
        public IReadOnlyList<string> EventDetails(string eventName) => EventField(eventName, "details");
        public IReadOnlyList<string> EventTriggers(string eventName) => EventField(eventName, "triggers");
        private IReadOnlyList<string> EventField(string name, string field)
            => Source.Fields.TryGetValue(name, out var value) && value.KindCase == Value.KindOneofCase.StructValue ? Strings(value.StructValue, field) : Array.Empty<string>();
        private static IReadOnlyList<string> Strings(Struct source, string field)
            => source.Fields.TryGetValue(field, out var value) && value.KindCase == Value.KindOneofCase.ListValue
                ? value.ListValue.Values.Where(x => x.KindCase == Value.KindOneofCase.StringValue).Select(x => x.StringValue).ToArray() : Array.Empty<string>();
        internal static IReadOnlyDictionary<string, string> StringMap(Struct source, string field)
            => source.Fields.TryGetValue(field, out var value) && value.KindCase == Value.KindOneofCase.StructValue
                ? value.StructValue.Fields.Where(x => x.Value.KindCase == Value.KindOneofCase.StringValue).ToDictionary(x => x.Key, x => x.Value.StringValue, StringComparer.Ordinal)
                : new Dictionary<string, string>();
    }
    public sealed class PolarSleepStatusEvent : EventArgs
    {
        public IReadOnlyList<PolarSleepRecordingStatus> Statuses { get; }
        public PolarSleepStatusEvent(IReadOnlyList<PolarSleepRecordingStatus> statuses) { Statuses = statuses; }
    }
}
