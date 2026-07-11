using Elarion.Abstractions;

// Opt the assembly into Elarion generation: the Market module's handlers, actors, and client-event
// topic are discovered here and published through the assembly manifest the host's bootstrapper reads.
[assembly: UseElarion]
