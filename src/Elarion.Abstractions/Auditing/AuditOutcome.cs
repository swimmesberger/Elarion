namespace Elarion.Abstractions.Auditing;

/// <summary>The recorded outcome of an audited handler invocation.</summary>
public enum AuditOutcome {
    /// <summary>The handler succeeded; for a command, the record committed atomically with the business writes.</summary>
    Succeeded,

    /// <summary>The handler returned a failure or threw; any transactional writes were rolled back.</summary>
    Failed,

    /// <summary>The caller was rejected by authorization (unauthenticated or forbidden) before the handler ran.</summary>
    Denied
}
