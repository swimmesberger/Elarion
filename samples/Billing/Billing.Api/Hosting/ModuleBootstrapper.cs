using Elarion.AspNetCore;

namespace Billing.Api.Hosting;

/// <summary>Opting in here makes the generator emit the cross-module wiring in the host assembly:
/// <c>ConfigureAllServices</c>, <c>MapAllEndpoints</c>, <c>RegisterRpcMethods</c>,
/// <c>GetAllJsonTypeInfoResolvers</c>, etc. — each feature-gated by <c>Modules:{Name}:Enabled</c>.</summary>
[GenerateModuleBootstrapper]
public static partial class ModuleBootstrapper;
