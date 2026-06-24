using System.Text.Json.Serialization;
using Billing.Application.Modules.Clients.Handlers;

namespace Billing.Application.Modules.Clients;

[JsonSerializable(typeof(CreateClient.Command), TypeInfoPropertyName = "CreateClientCommand")]
[JsonSerializable(typeof(CreateClient.Response), TypeInfoPropertyName = "CreateClientResponse")]
[JsonSerializable(typeof(GetClient.Query), TypeInfoPropertyName = "GetClientQuery")]
[JsonSerializable(typeof(GetClient.Response), TypeInfoPropertyName = "GetClientResponse")]
public sealed partial class ClientsJsonContext : JsonSerializerContext;
