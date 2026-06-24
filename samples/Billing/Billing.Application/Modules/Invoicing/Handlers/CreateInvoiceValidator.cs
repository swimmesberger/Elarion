using FluentValidation;

namespace Billing.Application.Modules.Invoicing.Handlers;

public sealed class CreateInvoiceValidator : AbstractValidator<CreateInvoice.Command> {
    public CreateInvoiceValidator() {
        RuleFor(x => x.AmountCents).GreaterThan(0);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.DueDate).NotEmpty();
    }
}
