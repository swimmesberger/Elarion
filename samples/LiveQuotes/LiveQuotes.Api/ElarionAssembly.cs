using Elarion.Abstractions;
using Elarion.AspNetCore;

// Opt the assembly into Elarion generation (handlers, actors, client-event topics) and emit the host's
// module bootstrapper (AddElarion, MapElarionEndpoints, …), each module gated by Modules:{Name}:Enabled.
[assembly: UseElarion]
[assembly: GenerateModuleBootstrapper]
