namespace Nebula.Core.Safety;

public enum CommandIntent
{
    SafeReadOnly,
    SafeWriteLocal,
    SafeExecuteLocal,
    PackageInstall,
    NetworkAccess,
    PrivilegedOperation,
    DestructiveOperation,
    DataExfiltration,
    NeedsApproval,
    Blocked,
    Unknown
}
