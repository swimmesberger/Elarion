using FluentValidation;

namespace Billing.Application.Modules.Clients.Handlers;

public sealed class CreateClientValidator : AbstractValidator<CreateClient.Command> {
    public CreateClientValidator() {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
    }
}
