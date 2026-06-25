namespace Billing.Application.Domain;

/// <summary>A billing client. Lives in the shared-kernel <c>Billing.Application.Domain</c> namespace
/// (under no <c>[AppModule]</c>), so every module may depend on it without tripping the module-boundary
/// analyzer (ELMOD002). The <c>OwnerId</c> scopes each row to the signed-in account. The entity carries no
/// marker — its <c>[EntityConfiguration]</c> (<see cref="Persistence.ClientConfiguration"/>) is what
/// drives its <c>DbSet</c> and schema.</summary>
public sealed class Client {
    public Guid Id { get; set; }
    public required string OwnerId { get; set; }
    public required string Number { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
