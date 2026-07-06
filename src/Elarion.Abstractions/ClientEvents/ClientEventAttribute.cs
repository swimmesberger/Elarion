namespace Elarion.Abstractions.ClientEvents;

/// <summary>
/// Overrides the topic name the generator infers for an <see cref="IClientEvent"/> contract. Without it, the
/// topic is <c>{module}.{name}</c> — the owning <c>[AppModule]</c>'s camel-cased name plus the camel-cased
/// type name (a trailing <c>Event</c> suffix stripped), e.g. <c>InvoiceChanged</c> in the Invoicing module →
/// <c>"invoicing.invoiceChanged"</c>. The override is the <b>full</b> topic name, like an explicit
/// <c>[Handler("module.action")]</c> name — use it to keep a wire name stable across a type rename.
/// </summary>
/// <param name="name">The full topic name (e.g. <c>"invoicing.invoiceChanged"</c>).</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ClientEventAttribute(string name) : Attribute {
    /// <summary>The full topic name clients subscribe to.</summary>
    public string Name { get; } = name;
}
