using Elarion.Abstractions.ClientEvents;

namespace Elarion.ClientEvents;

/// <summary>
/// Collects the client-event topics one <c>AddElarionClientEvents</c> call declares. Registration is
/// additive across calls (a future generator emits one call per module), and the composed catalog rejects
/// duplicate topic names and duplicate contract types when it is first resolved.
/// </summary>
public sealed class ClientEventsBuilder {
    private readonly List<ClientEventTopic> _topics = [];

    /// <summary>
    /// Declares <typeparamref name="TEvent"/> as the contract of topic <paramref name="name"/>
    /// (recommended shape: <c>{module}.{event}</c>). Nothing reaches the wire without this declaration —
    /// opt-in by enumeration.
    /// </summary>
    /// <typeparam name="TEvent">The client-event contract type.</typeparam>
    /// <param name="name">The topic name clients subscribe to.</param>
    /// <param name="configure">Optional subscribe-time requirements beyond "authenticated".</param>
    public ClientEventsBuilder AddTopic<TEvent>(string name, Action<ClientEventTopicOptions>? configure = null)
        where TEvent : class, IClientEvent {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var options = new ClientEventTopicOptions();
        configure?.Invoke(options);
        _topics.Add(new ClientEventTopic {
            Name = name,
            EventType = typeof(TEvent),
            Requirements = options.BuildRequirements(),
            AllowAnyResource = options.AllowsAnyResource,
            ObserverType = options.ObserverType,
            InterestLinger = options.InterestLinger ?? ClientEventTopic.DefaultInterestLinger,
        });
        return this;
    }

    internal IReadOnlyList<ClientEventTopic> Build() => _topics;
}
