namespace Elarion.Abstractions;

/// <summary>
/// Exports the public constants of a static class to clients: every <c>public const</c> field, <c>public static
/// readonly</c> field and <c>public static</c> get-only property of an exportable type — numbers, strings, booleans,
/// enums, and arrays or read-only lists of them — becomes <c>constants.{module}.{name}</c> in the exported schema and
/// <c>rpcConstants.{module}.{name}</c>, a literal type, in the generated client. The server and the client then read
/// one declaration, and a changed value reaches the client as a type change.
/// </summary>
/// <remarks>
/// <para>
/// The class must be <c>static</c>, not <c>private</c>, and under an <see cref="Modules.AppModuleAttribute"/> namespace
/// of its assembly: its module registers the constants, so they exist exactly when the module is enabled. The module
/// and member names are camel-cased as method names are (<c>Billing.MaxAmount</c> becomes
/// <c>rpcConstants.billing.maxAmount</c>); enum values are written with the canonical JSON configuration, as calls
/// write them. An unexportable public member, a mutable one, or two members of one module with the same exported name
/// is a build error (<c>ELRPC009</c> to <c>ELRPC011</c>); non-public members are not exported.
/// </para>
/// <para>
/// Export is a declaration: nothing else is published. Keep secrets and internal tuning out of such a class.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// namespace MyApp.Billing;
///
/// [ClientConstants]
/// public static class BillingLimits {
///     public const decimal MaxTransferAmount = 999_999.99m;
///     public const int MaxReceiptsPerInvoice = 10;
///     public static readonly IReadOnlyList&lt;string&gt; Currencies = ["EUR", "CHF"];
/// }
/// // TypeScript: rpcConstants.billing.maxReceiptsPerInvoice  (type 10)
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ClientConstantsAttribute : Attribute;
