using Elarion.EntityFrameworkCore;

namespace Billing.Domain;

/// <summary>A billing client. Lives in a separate domain assembly that references only the EF Core
/// marker package — the generator there emits the entity manifest the Application context reads.</summary>
[DbEntity]
public sealed class Client {
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
