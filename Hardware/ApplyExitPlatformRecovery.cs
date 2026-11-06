namespace MonitorFanControl;

internal static class ApplyExitPlatformRecovery
{
    internal static IApplyExitRecovery Create(Action<ApplyExitDiagnostic> diagnostic) => new ReferenceApplyExitRecovery(
        ProbeAsync, diagnostic: diagnostic);
    private static Task<bool> ProbeAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var bridges = HidFanController.DiagnoseLgInterfaces().Where(d =>
            d.Interface.Contains("vid_043e&pid_9c33&mi_00", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (bridges.Length == 0) return false;
        if (bridges.Length != 1 || bridges[0].Identity is not { VendorId: 0x043e, ProductId: 0x9c33, Version: 0x0111,
            UsagePage: 0xff00, Usage: 1, InputReportLength: 65, OutputReportLength: 65 })
            throw new MenuPreconditionException("The monitor USB bridge is present but its identity/descriptor cannot be confirmed.");
        return true;
    }, token);
}
