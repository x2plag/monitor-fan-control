using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace MonitorFanControl;
public static class ShortRampOsdAdmissionFile
{
    private sealed record ObservationRecord(string Kind, bool PhysicalOsdClosed, DateTimeOffset ObservedAtUtc, string Evidence);
    private sealed record StandingRecord(string Kind, DateTimeOffset DeclaredAtUtc, string Evidence, string Scope,
        string Status, string AssumedPhysicalOsdState, string QuestionPolicy, string ContraryPhysicalEvidence);
    private static readonly JsonSerializerOptions StandingJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static async Task<ShortRampOsdAdmission?> ReadAsync(string observationPath, CancellationToken token = default)
    {
        ShortRampOsdAdmission? admission = null;
            try
            {
                var info = new FileInfo(observationPath);
                if (!info.Exists || info.Length is <= 0 or > 16384) throw new InvalidDataException();
                var json = await File.ReadAllTextAsync(info.FullName, token);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("Kind", out var kind)) throw new InvalidDataException();
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (document.RootElement.EnumerateObject().Any(p => !names.Add(p.Name))) throw new InvalidDataException();
                if (kind.GetString() == "explicit-human-observation")
                {
                    if (names.Overlaps(["DeclaredAtUtc", "Status", "AssumedPhysicalOsdState", "QuestionPolicy", "ContraryPhysicalEvidence"]))
                        throw new InvalidDataException("Observation and standing instruction shapes cannot be mixed.");
                    var record = JsonSerializer.Deserialize<ObservationRecord>(json);
                    if (record is not { PhysicalOsdClosed: true }) throw new InvalidDataException();
                    admission = ShortRampOsdAdmission.FromObservation(new(record.ObservedAtUtc, record.Evidence));
                }
                else if (kind.GetString() == "standing-user-instruction")
                {
                    // The standing shape is deliberately exact: an observation
                    // field or duplicate cannot contradict its explicit premise.
                    var record = JsonSerializer.Deserialize<StandingRecord>(json, StandingJson);
                    if (record is not { Status: "active", AssumedPhysicalOsdState: "closed",
                        QuestionPolicy: "do-not-ask-again", ContraryPhysicalEvidence: "none" }) throw new InvalidDataException();
                    admission = new StandingOsdClosureInstruction(record.DeclaredAtUtc, record.Evidence, record.Scope,
                        StandingOsdInstructionStatus.Active, StandingOsdClosurePremise.Closed,
                        StandingOsdQuestionPolicy.DoNotAskAgain, StandingOsdContraryEvidence.None);
                }
                if (admission is not { IsValid: true }) admission = null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
            { admission = null; }
        return admission;
    }
}
