namespace Elarion.Features;

/// <summary>Options for the variant registry validator (<c>AddElarionVariantValidation</c>).</summary>
public sealed class VariantValidationOptions {
    /// <summary>
    /// When set, startup findings — a configured value no variant offers, or a platform variant contract with
    /// no DI registration — fail host startup instead of logging warnings. Re-validation on configuration
    /// reload always logs only (a runtime settings write must never crash a running host).
    /// </summary>
    public bool Strict { get; set; }
}
