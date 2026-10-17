namespace WorkHub.Core.Restore;

/// <summary>
/// §7.3: execution proceeds only if the execution-time class matches what the user confirmed. Otherwise the restore
/// aborts before anything is moved.
/// </summary>
public static class AcknowledgementRule
{
    public static bool Allows(DataClassification acknowledged, DataClassification execution) => acknowledged.Class switch
    {
        CurrentDataClass.Healthy => execution.Class == CurrentDataClass.Healthy && execution.Version == acknowledged.Version,
        CurrentDataClass.Degraded => execution.Class == CurrentDataClass.Degraded && execution.Sub == acknowledged.Sub && execution.Version == acknowledged.Version,
        CurrentDataClass.Unreadable => execution.Class == CurrentDataClass.Unreadable,
        CurrentDataClass.Missing => execution.Class == CurrentDataClass.Missing,
        // The dialog said "kept as found; a checked copy if possible": any readable outcome, but not a vanished file.
        CurrentDataClass.NeedsRecovery => execution.Class is CurrentDataClass.Healthy or CurrentDataClass.Degraded or CurrentDataClass.Unreadable,
        _ => false,
    };

    /// <summary>Classes that need the explicit checkbox in the confirmation (§13.2).</summary>
    public static bool NeedsExplicitAcknowledgement(DataClassification requestClass) =>
        requestClass.Class is CurrentDataClass.Degraded or CurrentDataClass.Unreadable or CurrentDataClass.NeedsRecovery;
}
