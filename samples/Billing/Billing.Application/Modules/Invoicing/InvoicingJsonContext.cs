using System.Text.Json.Serialization;
using Billing.Application.Modules.Invoicing.Handlers;

namespace Billing.Application.Modules.Invoicing;

// Explicit TypeInfoPropertyName per nested DTO keeps names unique within the generated context.
[JsonSerializable(typeof(CreateInvoice.Command), TypeInfoPropertyName = "CreateInvoiceCommand")]
[JsonSerializable(typeof(CreateInvoice.Response), TypeInfoPropertyName = "CreateInvoiceResponse")]
[JsonSerializable(typeof(ListInvoices.Query), TypeInfoPropertyName = "ListInvoicesQuery")]
[JsonSerializable(typeof(ListInvoices.Item), TypeInfoPropertyName = "InvoiceListItem")]
[JsonSerializable(typeof(ListInvoices.Response), TypeInfoPropertyName = "ListInvoicesResponse")]
[JsonSerializable(typeof(GetSendStatus.Query), TypeInfoPropertyName = "GetSendStatusQuery")]
[JsonSerializable(typeof(GetSendStatus.Response), TypeInfoPropertyName = "GetSendStatusResponse")]
public sealed partial class InvoicingJsonContext : JsonSerializerContext;
