namespace Elarion.Abstractions;

/// <summary>
/// Observes handlers that fail with an error their operation did not declare (see
/// <see cref="ProducesErrorAttribute"/>). Whether a handler body returns an undeclared code is not statically
/// knowable, so the contract is verified at runtime: the framework registers a monitor that logs each distinct
/// violation once in development and does nothing otherwise.
/// </summary>
public interface IErrorContractMonitor {
    /// <summary>
    /// Reports that <paramref name="operation"/> failed with <paramref name="error"/> although its contract does
    /// not declare that code (<paramref name="declared"/> is <see langword="null"/>) or declares it with a
    /// different kind (<paramref name="declared"/> is the mismatching declaration).
    /// </summary>
    void Violation(string operation, AppError error, ErrorContract? declared);
}
