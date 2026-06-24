using Billing.Application.Pipeline;
using Elarion.Abstractions;

// Opt the assembly into Elarion generation (handlers, services, validators, scheduled jobs, resilience
// policies, event consumers) and apply the default decorator pipeline assembly-wide.
[assembly: UseElarion]
[assembly: DefaultPipeline]
