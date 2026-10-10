using Billing.Application.Pipeline;
using Elarion.Abstractions;

// Opt the assembly into Elarion generation (handlers, services, validators, scheduled jobs, resilience
// policies, event consumers) and apply the default decorator pipeline assembly-wide.
[assembly: UseElarion]
[assembly: DefaultPipeline]

// Error-contract default (ADR-0080): lookups across the modules report a missing client, invoice or send job
// with the kind-default not_found code, so it is declared once for every operation in this assembly instead of
// on each handler. The schema and the generated client list it per method; an operation that never returns it
// only carries a variant its callers never see.
[assembly: ProducesError(ErrorKind.NotFound)]
