namespace Elarion.Abstractions.Resilience;

/// <summary>
/// Applies a named resilience policy to a generated handler or scheduled job invocation.
/// </summary>
/// <remarks>
/// On handlers and compile-time scheduled jobs, the policy executes inline around the current invocation.
/// Retry delays therefore keep the caller or scheduler run active until the pipeline succeeds, fails, or is cancelled.
/// Runtime scheduled jobs can alternatively use <see cref="Elarion.Abstractions.Scheduling.ScheduledJobOptions"/> to apply the same policy
/// with scheduler-deferred retry.
/// </remarks>
/// <param name="policyName">The stable policy name.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ResilientAttribute(string policyName) : Attribute {
    /// <summary>
    /// Stable policy name registered by a generated <see cref="ResiliencePolicyAttribute"/> type
    /// or manual resilience pipeline registration.
    /// </summary>
    public string PolicyName { get; } = policyName;
}
