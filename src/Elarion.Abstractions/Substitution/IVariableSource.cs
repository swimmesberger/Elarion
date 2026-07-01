namespace Elarion.Abstractions.Substitution;

/// <summary>
/// Supplies values for <see cref="VariableSubstitution"/> placeholders. This is the pluggable seam that
/// decouples variable substitution from any particular backend: an <c>IConfiguration</c> adapter ships
/// (<see cref="ConfigurationVariableSource"/>), and other sources (settings, environment, a dictionary) can
/// implement the same contract.
/// </summary>
public interface IVariableSource {
    /// <summary>
    /// Attempts to resolve <paramref name="key"/>. Returns <see langword="true"/> with the raw value (which
    /// may be empty or <see langword="null"/>) when the key is known, or <see langword="false"/> when it is
    /// absent. <see cref="VariableSubstitution"/> treats a missing, empty, or whitespace value as unset so an
    /// inline <c>:-</c> default can recover.
    /// </summary>
    bool TryGetValue(string key, out string? value);
}
