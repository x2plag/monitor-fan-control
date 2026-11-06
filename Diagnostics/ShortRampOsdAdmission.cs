namespace MonitorFanControl;

// An admission is either a time-limited observation or an explicit operating
// assumption. Neither can establish the physical state after software cleanup.
public sealed record ShortRampOsdAdmissionMetadata(string Kind, DateTimeOffset OriginalTimestampUtc,
    string Evidence, string Scope, string Basis);

public abstract record ShortRampOsdAdmission
{
    public const string WorkspaceScope = "lg39gx-diagnostic-this-workspace";
    internal abstract bool IsValid { get; }
    public abstract ShortRampOsdAdmissionMetadata Metadata { get; }

    public static ShortRampOsdAdmission? FromObservation(HumanOsdClosureObservation? observation) =>
        observation is null ? null : new ExplicitObservation(observation);

    private sealed record ExplicitObservation(HumanOsdClosureObservation Observation) : ShortRampOsdAdmission
    {
        internal override bool IsValid => Observation.IsCurrent;
        public override ShortRampOsdAdmissionMetadata Metadata => new("explicit-human-observation",
            Observation.ObservedAtUtc, Observation.Evidence, WorkspaceScope,
            "Direct explicit human observation before admission; original observation time retained; no post-cleanup physical verification.");
    }
}

public enum StandingOsdInstructionStatus { Unknown, Active, Revoked }
public enum StandingOsdClosurePremise { Unknown, Closed, Open }
public enum StandingOsdQuestionPolicy { Unknown, DoNotAskAgain, Ask }
public enum StandingOsdContraryEvidence { Unknown, None, Present }

public sealed record StandingOsdClosureInstruction(DateTimeOffset DeclaredAtUtc, string Evidence, string Scope,
    StandingOsdInstructionStatus Status, StandingOsdClosurePremise AssumedPhysicalOsdState,
    StandingOsdQuestionPolicy QuestionPolicy, StandingOsdContraryEvidence ContraryPhysicalEvidence) : ShortRampOsdAdmission
{
    internal override bool IsValid => DeclaredAtUtc != default && DeclaredAtUtc <= DateTimeOffset.UtcNow &&
        !string.IsNullOrWhiteSpace(Evidence) && Scope == WorkspaceScope &&
        Status == StandingOsdInstructionStatus.Active && AssumedPhysicalOsdState == StandingOsdClosurePremise.Closed &&
        QuestionPolicy == StandingOsdQuestionPolicy.DoNotAskAgain && ContraryPhysicalEvidence == StandingOsdContraryEvidence.None;
    public override ShortRampOsdAdmissionMetadata Metadata => new("standing-user-instruction", DeclaredAtUtc,
        Evidence, Scope, "User-directed standing assumption of closed OSD; not a fresh physical observation or post-cleanup physical verification; no renewed OSD reply requested.");
}
