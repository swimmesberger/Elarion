using Elarion.Abstractions.ClientEvents;

namespace LiveQuotes.Application.Modules.Market;

/// <summary>
/// Resource-scoped subscriptions are <b>fail-closed</b>: without an authorizer, subscribing
/// <c>{topic: "market.quoteChanged", resource: "ELN"}</c> reads as 404. Quotes are not per-user data,
/// so this one allows any authenticated user to watch any symbol; an app with per-tenant entitlements
/// checks them here (inject <c>ICurrentUser</c> and whatever the entitlement source is — this runs in
/// the request scope like any scoped service).
/// </summary>
public sealed class MarketSubscriptionAuthorizer : IClientEventSubscriptionAuthorizer {
    public ValueTask<bool> AuthorizeAsync(ClientEventSubscription subscription, CancellationToken ct) =>
        ValueTask.FromResult(true);
}
