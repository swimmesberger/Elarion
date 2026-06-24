using Elarion.Abstractions;

// Turns on the framework's assembly-level generators for this project: module handlers, services,
// validators, scheduled jobs, and event consumers. Without it the per-module ConfigureDefaultServices
// skeleton is still emitted, but its AddHandlers/AddServices/... hooks have no implementation, so
// nothing is registered.
[assembly: UseElarion]
