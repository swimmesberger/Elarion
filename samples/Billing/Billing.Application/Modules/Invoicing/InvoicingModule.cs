using System.Text.Json.Serialization.Metadata;
using Elarion.Abstractions.Modules;

namespace Billing.Application.Modules.Invoicing;

/// <summary>A feature module — enabled by default, switchable with <c>Modules:Invoicing:Enabled</c>.
/// When disabled, its handlers, endpoints, JSON metadata, and scheduled jobs all disappear together.</summary>
[AppModule("Invoicing")]
public static partial class InvoicingModule {
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() {
        return InvoicingJsonContext.Default;
    }
}
