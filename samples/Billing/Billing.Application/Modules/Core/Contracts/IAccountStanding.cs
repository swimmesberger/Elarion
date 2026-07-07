using Elarion.Abstractions;
using Elarion.Abstractions.Modules;

namespace Billing.Application.Modules.Core.Contracts;

/// <summary>The Core module's published <em>account-standing</em> policy — the domain rule that decides
/// whether a customer may be invoiced right now (e.g. their outstanding balance would exceed a credit limit).
/// This is a genuine cross-module domain call: the <c>Invoicing</c> module consults it before raising an
/// invoice, and Core owns the policy. It is published as a <c>[ModuleContract]</c> — the sanctioned way one
/// module depends on another (ADR-0002) — so Invoicing calls a stable, intent-revealing surface instead of
/// reaching into Core's internals or reimplementing the rule. The implementation stays Core-internal.
///
/// <para>Contrast with the two mechanisms it is <em>not</em>: it is not a platform <em>port</em> (that is for
/// intent-only infrastructure like sending email), and it is not framework-provided — this is bespoke billing
/// policy the app owns. Cross-cutting concerns the framework already solves (auditing via <c>[Auditable]</c>,
/// validation, authorization) never need a contract like this.</para></summary>
[ModuleContract]
public interface IAccountStanding {
    /// <summary>Returns a failed <see cref="Result"/> (a <c>BusinessRule</c> error) when invoicing
    /// <paramref name="clientId"/> for <paramref name="amountCents"/> would breach the customer's credit
    /// limit; otherwise <see cref="Result.Success"/>.</summary>
    ValueTask<Result> EnsureCanInvoiceAsync(Guid clientId, long amountCents, CancellationToken ct = default);
}
