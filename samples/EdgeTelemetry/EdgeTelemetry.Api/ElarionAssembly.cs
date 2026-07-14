using Elarion.Abstractions;
using Elarion.AspNetCore;
using Elarion.Sql;

// Opt the assembly into Elarion generation (handler registrations, the permission catalog, …) and
// emit the host's module bootstrapper (AddElarion), each module gated by Modules:{Name}:Enabled.
[assembly: UseElarion]
[assembly: GenerateModuleBootstrapper]

// PostgreSQL provider trigger for the SQL mapper generator: [SqlJson] parameters bind as jsonb
// (a plain string parameter would fail PostgreSQL's type check for the meta column).
[assembly: UseElarionSql(Provider = SqlProvider.Npgsql)]
