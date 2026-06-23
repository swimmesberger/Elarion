namespace Elarion.Abstractions.Modules;

/// <summary>
/// Configures how a handler participates in its module's generated typed in-process API
/// (see <see cref="GenerateModuleApiAttribute"/>).
/// </summary>
/// <remarks>
/// <para>
/// This attribute is a <em>configurator, never a gate</em>. Every handler in a module is included
/// in that module's default (unscoped) <c>[GenerateModuleApi]</c> facade automatically — handler
/// participation is opt-out, mirroring how every handler is in-process callable by nature. Apply
/// this attribute only to:
/// </para>
/// <list type="bullet">
///   <item><description>exclude a handler from every facade (<see cref="Exclude"/> = <c>true</c>); or</description></item>
///   <item><description>tag a handler into one or more named <see cref="Scopes"/> so it also appears
///     on the matching scoped facades.</description></item>
/// </list>
/// <para>
/// The scope vocabulary mirrors <c>[DbEntity]</c>/<c>[GenerateDbSets]</c>: a handler's scope tags are
/// additive (it stays in the default facade), and a scoped facade selects the handlers whose tags
/// intersect its own. A handler with no attribute is in the default facade only.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ModuleApiAttribute(params string[] scopes) : Attribute
{
    /// <summary>
    /// The named scopes this handler is tagged into. Empty means the handler participates only in the
    /// module's default (unscoped) facade.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; } = scopes;

    /// <summary>
    /// When <c>true</c>, the handler is excluded from every generated facade, scoped or default.
    /// </summary>
    public bool Exclude { get; init; }
}
