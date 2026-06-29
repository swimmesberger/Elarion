using Elarion.AspNetCore;

// Opting in here makes the generator emit the host's cross-module wiring as the fixed-name ElarionBootstrapper
// static — AddElarion, MapElarionEndpoints, RegisterHandlers, GetMcpMetadata, GetAllJsonTypeInfoResolvers, … —
// each feature-gated by Modules:{Name}:Enabled. The type is framework-named (ADR-0016); you never declare it.
[assembly: GenerateModuleBootstrapper]
