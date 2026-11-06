namespace MonitorFanControl;

public sealed record ShortRampCommandRequest(string ObservationPath, ShortRampPurpose Purpose);
public sealed record ShortRampChildCommand(string Role, string Nonce, int ParentPid, ShortRampPurpose Purpose);

// Parsing is separate from execution so exact admission can be checked offline
// without attaching a console or constructing any capture/device resources.
public static class ShortRampCommand
{
    public static bool TryParseContextualHold(string[] args, out ShortRampCommandRequest? request)
    {
        request = null;
        if (args.Length != 2 || args[0] != "--test39-contextual-hold-audio" || string.IsNullOrWhiteSpace(args[1]) ||
            args[1].StartsWith("--", StringComparison.Ordinal)) return false;
        request = new(args[1], ShortRampPurpose.ContextualClosedHold);
        return true;
    }

    public static bool TryParseClosedHold(string[] args, out ShortRampCommandRequest? request)
    {
        request = null;
        if (args.Length != 2 || args[0] != "--test39-closed-hold-audio" || string.IsNullOrWhiteSpace(args[1]) ||
            args[1].StartsWith("--", StringComparison.Ordinal)) return false;
        request = new(args[1], ShortRampPurpose.BoundedClosedHold);
        return true;
    }

    public static bool TryParseControl(string[] args, out ShortRampCommandRequest? request)
    {
        request = null;
        if (args.Length != 2 || args[0] != "--test39-short-control-audio" || string.IsNullOrWhiteSpace(args[1]) ||
            args[1].StartsWith("--", StringComparison.Ordinal)) return false;
        request = new(args[1], ShortRampPurpose.NoReductionControl);
        return true;
    }

    public static bool TryParseChild(string[] args, out ShortRampChildCommand? request)
    {
        request = null;
        if (args.Length is not (3 or 4) || args[0] is not ("--short39-writer" or "--short39-guardian") ||
            !Guid.TryParseExact(args[1], "N", out _) || !int.TryParse(args[2], out var parentPid) || parentPid <= 0)
            return false;
        ShortRampPurpose purpose;
        try { purpose = args.Length == 3 ? ShortRampPurpose.Mode2 : ShortRampPurposePolicy.Parse(args[3]); }
        catch (System.IO.InvalidDataException) { return false; }
        request = new(args[0], args[1], parentPid, purpose);
        return true;
    }
}

// Recognize malformed apply requests too, so they cannot fall through to the
// ordinary resident application and acquire a different control lifecycle.
public static class ApplyExitCommand
{
    public static bool IsRequested(string[] args) => args.Contains("--lg39-apply-exit", StringComparer.Ordinal);
    public static bool IsValid(string[] args) => args.SequenceEqual(new[] { "--lg39-apply-exit" });
}
