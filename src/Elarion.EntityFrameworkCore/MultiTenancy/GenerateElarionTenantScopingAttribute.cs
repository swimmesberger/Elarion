namespace Elarion.EntityFrameworkCore;

/// <summary>
/// Applies ambient tenant scoping to the annotated partial <c>DbContext</c>: the generator implements the EF
/// model-configuration seam with a call to <c>ApplyElarionTenantScoping</c>, which attaches a named query
/// filter to every entity implementing <c>ITenantScoped&lt;TTenantId&gt;</c>. Requires
/// <c>[GenerateDbSets]</c> on the same context (<c>ELTEN001</c>).
/// </summary>
/// <remarks>
/// The attribute covers the <b>read</b> leg only. Register
/// <c>AddElarionTenantScopingEntityFrameworkCore&lt;TDbContext&gt;()</c> as well, which supplies the tenant the
/// filter reads and attaches the insert-time stamp; without it the context has no tenant in scope and every
/// tenant-scoped query fails closed. A host that writes its own <c>OnModelCreating</c> can call
/// <c>modelBuilder.ApplyElarionTenantScoping(this)</c> directly instead of using this attribute.
/// </remarks>
/// <example>
/// <code>
/// [GenerateDbSets]
/// [GenerateElarionTenantScoping]
/// public partial class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options) : DbContext(options) {
///     protected override void OnModelCreating(ModelBuilder modelBuilder) =&gt; ConfigureEntities(modelBuilder);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateElarionTenantScopingAttribute : Attribute;
