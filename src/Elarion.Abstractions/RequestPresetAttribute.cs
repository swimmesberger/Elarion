namespace Elarion.Abstractions;

/// <summary>
/// Declares a named request preset: a <c>static readonly</c> field of a <see cref="HandlerAttribute"/> handler that
/// holds a value of the handler's request type — a list's quick filter, a wizard's default. The exported schema carries
/// the preset under the method (<c>presets.{name}</c>, the members that differ from the request's defaults, in the
/// canonical JSON form), and the generated client offers it as <c>rpcPresets['{method}'].{name}</c>, typed as the
/// method's params. A client that opens what the server counted sends exactly the instance the server counted with.
/// </summary>
/// <remarks>
/// <para>
/// The field must be <c>static readonly</c>, declared on the handler class, and of the handler's request type; the
/// handler must be on JSON-RPC, because a preset belongs to a method. Each name is unique per handler. Anything else is
/// a build error (<c>ELRPC006</c> to <c>ELRPC008</c>). The schema export reads the field when it runs at build time,
/// so a preset costs nothing at run time.
/// </para>
/// <para>
/// A preset carries only request values. Labels belong to the client's language files and counts to the operation that
/// counts; pass the same field to it (<c>ListInvoices.ToTransfer</c>) so the count and the list cannot drift.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Handler]
/// public sealed class ListInvoices(AppDbContext db) : IHandler&lt;ListInvoices.Query, Result&lt;Page&lt;InvoiceRow&gt;&gt;&gt; {
///     [RequestPreset("toTransfer")]
///     public static readonly Query ToTransfer = new() { ToTransfer = true };
///
///     [RequestPreset("notExported")]
///     public static readonly Query NotExported = new() { Status = InvoiceStatus.Approved, Exported = false };
///
///     public sealed record Query : OffsetPageRequest, IQuery { … }
///     …
/// }
/// // TypeScript: rpc.invoices.list({ ...rpcPresets['invoices.list'].toTransfer, page: 2 })
/// </code>
/// </example>
/// <param name="name">The preset's name, as the client reads it (for example <c>"toTransfer"</c>).</param>
[AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class RequestPresetAttribute(string name) : Attribute {
    /// <summary>The preset's name, as the client reads it.</summary>
    public string Name { get; } = name;
}
