namespace Elarion.EntityFrameworkCore;

/// <summary>
/// Configures Elarion's Entity Framework Core source generation for the annotated assembly. The EF
/// Core analogue of <c>[assembly: UseElarion]</c>: place it once on the application assembly to
/// declare the target database provider so generators can emit provider-optimized variants.
/// </summary>
/// <remarks>
/// When omitted, generators behave as if <see cref="Provider"/> were
/// <see cref="EfCoreProvider.Portable"/> and emit provider-neutral code. Setting
/// <see cref="Provider"/> to <see cref="EfCoreProvider.Npgsql"/> opts generated keyset seek
/// predicates into PostgreSQL row-value comparisons; the generated code then depends on the Npgsql
/// EF Core provider being referenced by the application.
/// </remarks>
/// <example>
/// <code>
/// [assembly: Elarion.EntityFrameworkCore.UseElarionEntityFrameworkCore(
///     Provider = Elarion.EntityFrameworkCore.EfCoreProvider.Npgsql)]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class UseElarionEntityFrameworkCoreAttribute : Attribute
{
    /// <summary>
    /// Gets the target database provider. Defaults to <see cref="EfCoreProvider.Portable"/>.
    /// </summary>
    public EfCoreProvider Provider { get; init; } = EfCoreProvider.Portable;
}
