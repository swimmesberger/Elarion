namespace Elarion.Abstractions.ClientEvents;

/// <summary>
/// Marks a type as a <em>client event</em>: the schema of a client-facing topic — a deliberate wire contract
/// at the trust boundary, the client-side analogue of <c>[ModuleContract]</c>.
/// </summary>
/// <remarks>
/// <para>
/// A client event is <b>not</b> a third event plane. Domain and integration events encode the relationship to
/// the database transaction (the one distinction a publisher cannot delegate); a client event is a
/// <em>projection</em> of an after-commit fact onto an audience outside the trust boundary. The recommended
/// producer is a method-form <c>[ConsumeEvent]</c> on a <c>[Service]</c> that maps the internal integration
/// event to this contract and publishes via <see cref="IClientEventPublisher"/> — integration consumers run
/// after commit, so a rolled-back command can never push a ghost update.
/// </para>
/// <para>
/// Delivery to a client is <b>at-most-once, a hint</b>: keep the payload light (ids and refs, never state) and
/// let the client converge by re-running normal query handlers, which carry the real authorization gates. An
/// internal integration event must never implement this interface directly — publish a separate, deliberate
/// contract so internal renames cannot break deployed frontends (ADR-0042).
/// </para>
/// </remarks>
public interface IClientEvent;
