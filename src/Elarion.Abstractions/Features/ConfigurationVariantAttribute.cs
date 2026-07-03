namespace Elarion.Abstractions.Features;

/// <summary>
/// Marks a <c>[Service]</c> as a <i>variant implementation</i> selected at run time by a configuration value —
/// the deterministic, process-global sibling of <see cref="FeatureVariantAttribute"/>. It is a <b>modifier on a
/// service registration</b>: the class must also carry <c>[Service]</c> (which declares the service, its
/// contract(s), and its lifetime), and <c>[ConfigurationVariant]</c> only changes how those contracts are
/// resolved — keyed by the configured value instead of plain. The contract is therefore <b>not</b> repeated
/// here; it is whatever the <c>[Service]</c> registers under (its implemented interfaces, or explicit
/// <c>[Service(typeof(...))]</c> types), and a service registering under several contracts is variant-resolved
/// on each.
/// </summary>
/// <remarks>
/// <example>
/// <code>
/// [Service]
/// [ConfigurationVariant("Email:Backend")]                      // the default (no Value)
/// public sealed class SmtpEmailSender : IEmailSender { ... }
///
/// [Service]
/// [ConfigurationVariant("Email:Backend", Value = "office365")]
/// public sealed class Office365EmailSender : IEmailSender { ... }
/// </code>
/// </example>
/// Selection reads <see cref="Key"/> through <c>IConfiguration</c>, so any configuration provider drives it:
/// <c>appsettings.json</c> (with <c>reloadOnChange</c> for runtime switching), environment variables, or the
/// Elarion settings bridge (<c>AddElarionSettingsConfiguration</c>), which makes the value admin-writable at
/// run time and propagates changes cluster-wide. Because the read is synchronous, resolution involves no async
/// proxy and no per-scope warm-up: consumers — handlers, services, any scoped or transient class — inject the
/// contract directly, and each new DI scope observes the current value (work already in flight keeps the
/// implementation it started with). The configured value is matched case-insensitively; an absent key or a
/// value matching no variant resolves the default implementation. Contrast with
/// <see cref="FeatureVariantAttribute"/>, whose selection is per-user (a feature flag's allocated variant) and
/// therefore asynchronous and per-scope-warmed. A <c>[ConfigurationVariant]</c> without <c>[Service]</c> is
/// reported (<c>ELVAR007</c>), and a contract bound by both attributes is rejected (<c>ELVAR008</c>).
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConfigurationVariantAttribute(string key) : Attribute {
    /// <summary>The configuration key whose value selects the implementation (e.g. <c>"Email:Backend"</c>).</summary>
    public string Key { get; } = key;

    /// <summary>
    /// The configured value this implementation is selected for (matched case-insensitively). When omitted,
    /// this implementation is the <i>default</i>, used when the key is absent or its value matches no variant.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Marks this implementation as the default <i>in addition to</i> its <see cref="Value"/> — a <b>named
    /// default</b>: it is selected when the key is absent or matches nothing, and also explicitly selectable
    /// by its value. Prefer this over an unnamed default for admin-facing switches, so every state — including
    /// the default — has a writable, validatable name (e.g. <c>Value = "smtp", IsDefault = true</c> lets an
    /// admin switch back to SMTP by writing <c>"smtp"</c> rather than by removing the key).
    /// </summary>
    public bool IsDefault { get; set; }
}
