using Elarion.Abstractions.Modules;

namespace Billing.Application.Modules.Core;

/// <summary>The always-on foundation module. <c>Kind = AppModuleKind.Core</c> keeps it enabled
/// regardless of configuration and initialized before feature modules. It owns shared domain capabilities
/// other modules build on — such as the account-standing (credit) policy, published as a
/// <c>[ModuleContract]</c> that <c>Invoicing</c> consults before raising an invoice.</summary>
[AppModule("Core", Kind = AppModuleKind.Core)]
public static partial class CoreModule;
