using System.Text.Json.Serialization;
using Billing.Application.Modules.Clients.Handlers;

namespace Billing.Application.Modules.Clients;

// Nested handler DTOs share short names (Command/Query/Response), so each needs an explicit
// TypeInfoPropertyName to stay unique within the single generated JsonSerializerContext.
[JsonSerializable(typeof(CreateClient.Command), TypeInfoPropertyName = "CreateClientCommand")]
[JsonSerializable(typeof(CreateClient.Response), TypeInfoPropertyName = "CreateClientResponse")]
[JsonSerializable(typeof(GetClient.Query), TypeInfoPropertyName = "GetClientQuery")]
[JsonSerializable(typeof(GetClient.Response), TypeInfoPropertyName = "GetClientResponse")]
[JsonSerializable(typeof(ListClients.Query), TypeInfoPropertyName = "ListClientsQuery")]
[JsonSerializable(typeof(ListClients.Item), TypeInfoPropertyName = "ClientListItem")]
[JsonSerializable(typeof(ListClients.Response), TypeInfoPropertyName = "ListClientsResponse")]
public sealed partial class ClientsJsonContext : JsonSerializerContext;
