using System.ComponentModel;
using Billing.Application.Domain;
using Billing.Application.Modules.Core.Contracts;
using Billing.Application.Modules.Invoicing.Events;
using Billing.Application.Modules.Invoicing.Jobs;
using Billing.Application.Persistence;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Caching;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Messaging;
using Elarion.Abstractions.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Modules.Invoicing.Handlers;

/// <summary>Persists a <c>Draft</c> invoice and enqueues the send job in deferred-retry mode, returning
/// the stable <c>JobId</c> so the caller can poll progress. Requires the <c>invoices:write</c> permission.
/// The <see cref="InvoiceCreated"/> integration event is recorded in the same unit of work — it commits
/// with the invoice, or not at all.</summary>
[Handler("invoices.create")]
[RequirePermission("invoices:write")]
[CacheInvalidate("invoices")]
[Description("Creates a draft invoice and sends it to the client in the background.")]
public sealed class CreateInvoice(
    BillingDbContext db,
    ICurrentUser user,
    IJobScheduler scheduler,
    IIntegrationEventBus integrationEvents,
    IAuditTrail audit,
    TimeProvider clock
) : IHandler<CreateInvoice.Command, Result<CreateInvoice.Response>> {
    public sealed record Command : ICommand {
        public required Guid ClientId { get; init; }
        [Description("Invoice total in minor units, e.g. 1999 = €19.99.")]
        public required long AmountCents { get; init; }
        public required string Currency { get; init; }
        public required DateOnly DueDate { get; init; }
    }

    public sealed record Response(Guid InvoiceId, string Number, Guid SendJobId);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var clientExists = await db.Clients
            .AnyAsync(c => c.OwnerId == user.UserId && c.Id == command.ClientId, ct);
        if (!clientExists) {
            return AppError.NotFound($"Client {command.ClientId} was not found.");
        }

        var count = await db.Invoices.CountAsync(i => i.OwnerId == user.UserId, ct);
        var invoice = new Invoice {
            Id = Guid.NewGuid(),
            OwnerId = user.UserId,
            ClientId = command.ClientId,
            Number = $"INV-{count + 1:D6}",
            AmountCents = command.AmountCents,
            Currency = command.Currency,
            Status = InvoiceStatus.Draft,
            DueDate = command.DueDate,
            CreatedAt = clock.GetUtcNow(),
        };

        db.Invoices.Add(invoice);
        // Record the integration event in the same unit of work — it commits with the invoice, or not at all.
        await integrationEvents.PublishAsync(new InvoiceCreated(invoice.Id), ct);
        await db.SaveChangesAsync(ct);

        var handle = await scheduler.EnqueueAsync<SendInvoiceEmailJob, SendInvoiceEmailPayload>(
            new SendInvoiceEmailPayload { InvoiceId = invoice.Id },
            new ScheduledJobOptions {
                ResiliencePolicy = InvoiceEmailPolicy.Reference,
                ResilienceMode = ScheduledJobResilienceMode.DeferredRetry,
                CorrelationId = invoice.Id.ToString(),
            },
            ct);

        await audit.RecordAsync("invoice.created", invoice.Id.ToString(), ct);
        return new Response(invoice.Id, invoice.Number, handle.JobId);
    }
}
