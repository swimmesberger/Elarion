### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
ELSG001 | Elarion.Generators | Error | Hosted service scope must be singleton
ELSG002 | Elarion.Generators | Error | Invalid explicit service contract
ELSG003 | Elarion.Generators | Error | Generic services are not supported
ELSG004 | Elarion.Generators | Error | Invalid scheduled job method signature
ELSG005 | Elarion.Generators | Error | Generic scheduled job type is not supported
ELSG006 | Elarion.Generators | Error | Invalid runtime scheduled job type
ELSG007 | Elarion.Generators | Error | Duplicate scheduled job name
ELSG008 | Elarion.Generators | Error | Invalid schedule specification
ELSG009 | Elarion.Generators | Error | Invalid scheduled job concurrency
ELEVT001 | Elarion.Generators | Error | Event consumer must be declared on a [Service] class
ELEVT002 | Elarion.Generators | Error | Invalid event consumer signature
ELEVT004 | Elarion.Generators | Error | Duplicate request responder
ELMCP002 | Elarion.Mcp | Warning | Duplicate MCP tool name under the default tool-name transform
ELMCP003 | Elarion.Mcp | Warning | MCP customization is ignored when the handler excludes MCP
ELHTTP001 | Elarion.Http | Warning | HTTP endpoint handler is missing a request/response shape
ELHTTP002 | Elarion.Http | Warning | Duplicate HTTP endpoint route
ELHTTP003 | Elarion.Http | Warning | HTTP endpoint handler is not in any module
ELMOD001 | Elarion.Modules | Warning | Multiple app modules share a namespace
ELRPC001 | Elarion.JsonRpc | Warning | RPC method handler is not in any module
WFRE001 | Elarion.Abstractions.Resilience | Error | Invalid resilience policy
WFRE002 | Elarion.Abstractions.Resilience | Error | Duplicate resilience policy name
WIMCACHE001 | Elarion.Abstractions.Caching | Error | Handler cannot be both cacheable and cache-invalidating
WIMCACHE002 | Elarion.Abstractions.Caching | Error | Handler cache tags are required
WIMCACHE003 | Elarion.Abstractions.Caching | Error | Handler cache tag is invalid
WIMCACHE004 | Elarion.Abstractions.Caching | Error | Handler cache duration is invalid
