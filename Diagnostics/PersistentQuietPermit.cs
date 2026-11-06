namespace MonitorFanControl;

public sealed partial class ShortRampPermit
{
    public const int PersistentRecoveryMs = 55000;
    public const int PersistentPreCloseMs = 1000;
    public const int PersistentHealthLeaseMs = 2000;
    public bool IsPersistent => Purpose is ShortRampPurpose.PersistentQuiet or ShortRampPurpose.ReferencePersistentQuiet;
    public bool UsesContextualRecovery => Purpose is ShortRampPurpose.ContextualClosedHold or ShortRampPurpose.PersistentQuiet or ShortRampPurpose.ReferencePersistentQuiet;
    public bool PersistentQuietActive => IsPersistent && State is ShortRampPermitState.Armed or ShortRampPermitState.Used &&
        _view.ReadInt32(224) == 3 && _view.ReadInt64(272) > 0;
    public long PersistentStopQpc => IsPersistent ? _view.ReadInt64(576) : 0;
    internal long PersistentRecoveryProgressQpc => IsPersistent ? Math.Max(PersistentStopQpc, _view.ReadInt64(592)) : 0;
    internal void RecordPersistentRecoveryProgress() { if (IsPersistent) _view.Write(592, _clock.Timestamp); }
    public bool PersistentHealthExpired => IsPersistent && PersistentQuietActive &&
        _clock.Timestamp >= _view.ReadInt64(584) + Ticks(PersistentHealthLeaseMs);
    internal void RenewPersistentHealth()
    {
        if (ReferenceReleased) return;
        if (PersistentHealthExpired)
        {
            Revoke();
            throw new InvalidOperationException("Expired health cannot renew Quiet.");
        }
        if (!IsPersistent || State is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup)
            throw new InvalidOperationException("A health response cannot resurrect revoked Quiet.");
        _view.Write(584, _clock.Timestamp);
    }

    // Aligned shared CAS freezes the first stop without waiting for any actor's
    // mutex. A killed/suspended owner cannot strand revocation or renew recovery.
    private unsafe void FreezePersistentStop()
    {
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try { Interlocked.CompareExchange(ref *(long*)(pointer + _view.PointerOffset + 576), Math.Max(1, _clock.Timestamp), 0); }
        finally { _view.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }
}
