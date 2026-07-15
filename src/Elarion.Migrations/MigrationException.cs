namespace Elarion.Migrations;

/// <summary>
/// A migration configuration or validation failure: invalid script resources, a checksum mismatch
/// against applied history, an out-of-order script under <see cref="OutOfOrderPolicy.Deny"/>, or an
/// invalid runner argument. The message names the offending script and the legal resolutions.
/// </summary>
public class MigrationException : Exception {
    /// <summary>Creates the exception with a message describing the failure and its resolutions.</summary>
    public MigrationException(string message) : base(message) {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public MigrationException(string message, Exception innerException) : base(message, innerException) {
    }
}
