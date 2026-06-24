using Elarion.Abstractions.Modules;

namespace Billing.Application.Modules.Core;

/// <summary>The always-on foundation module. <c>Kind = AppModuleKind.Core</c> keeps it enabled
/// regardless of configuration and initialized before feature modules. It owns shared services such as
/// the audit trail.</summary>
[AppModule("Core", Kind = AppModuleKind.Core)]
public static partial class CoreModule;
