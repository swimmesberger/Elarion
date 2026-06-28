namespace Elarion.Settings;

/// <summary>The outcome of a settings write.</summary>
public enum SettingWriteStatus {
    /// <summary>The write was applied.</summary>
    Success,

    /// <summary>
    /// The write was rejected because the caller's expected version did not match the stored version.
    /// </summary>
    ConcurrencyConflict
}

/// <summary>
/// The result of a settings write. Failures are returned as data rather than thrown, so the store seam stays
/// exception-free across providers (mirroring the EF Core outbox store).
/// </summary>
/// <param name="Status">Whether the write succeeded or hit a concurrency conflict.</param>
/// <param name="Version">The new version on success; <c>0</c> on conflict.</param>
public readonly record struct SettingWriteResult(SettingWriteStatus Status, int Version) {
    /// <summary>Whether the write was applied.</summary>
    public bool IsSuccess => Status == SettingWriteStatus.Success;

    /// <summary>Creates a successful result carrying the new version.</summary>
    public static SettingWriteResult Success(int version) => new(SettingWriteStatus.Success, version);

    /// <summary>A result indicating the expected version did not match.</summary>
    public static SettingWriteResult ConcurrencyConflict { get; } = new(SettingWriteStatus.ConcurrencyConflict, Version: 0);
}
