### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
ELPIPE001 | Elarion.Generators | Error | Decorator AppliesTo predicate must be public
ELPIPE002 | Elarion.Generators | Error | Decorator AppliesTo predicate has an unsupported signature
ELSG001 | Elarion.Generators | Error | Hosted service scope must be singleton
ELSG002 | Elarion.Generators | Error | Invalid explicit service contract
ELSG003 | Elarion.Generators | Error | Generic services are not supported
ELSG004 | Elarion.Generators | Error | Invalid scheduled job method signature
ELSG005 | Elarion.Generators | Error | Generic scheduled job type is not supported
ELSG006 | Elarion.Generators | Error | Invalid runtime scheduled job type
ELSG007 | Elarion.Generators | Error | Duplicate scheduled job name
ELSG008 | Elarion.Generators | Error | Invalid schedule specification
ELSG009 | Elarion.Generators | Error | Invalid scheduled job concurrency
ELSG010 | Elarion.Generators | Warning | Scheduled job is not in any module
ELEVT001 | Elarion.Generators | Error | Event consumer must be declared on a [Service] class
ELEVT002 | Elarion.Generators | Error | Invalid event consumer signature
ELEVT003 | Elarion.Generators | Warning | Event consumer is not in any module
ELEVT004 | Elarion.Generators | Error | Duplicate request responder
ELEVT005 | Elarion.Generators | Error | Invalid handler-form event consumer
ELMCP003 | Elarion.Mcp | Warning | MCP customization is ignored when the handler excludes MCP
ELHTTP001 | Elarion.Http | Warning | HTTP endpoint handler has no resolvable request/response shape
ELHTTP002 | Elarion.Http | Warning | Duplicate HTTP endpoint route
ELHTTP003 | Elarion.Http | Warning | HTTP endpoint handler is not in any module
ELHTTP004 | Elarion.Http | Warning | Cannot infer HTTP verb
ELMOD001 | Elarion.Modules | Warning | Multiple app modules share a namespace
ELMOD002 | Elarion.Modules | Warning | Cross-module reference to a module-internal type
ELAPI001 | Elarion.Modules | Error | Module API interface must be partial
ELAPI002 | Elarion.Modules | Error | Module API interface must be top-level
ELAPI003 | Elarion.Modules | Warning | Module API interface is not in any module
ELAPI004 | Elarion.Modules | Error | Duplicate module API method
ELRPC001 | Elarion.JsonRpc | Warning | RPC method handler is not in any module
ELRPC002 | Elarion.JsonRpc | Warning | RPC handler has no resolvable request/response shape
ELRPC003 | Elarion.JsonRpc | Warning | Duplicate operation name across the handler bus
ELRES001 | Elarion.Abstractions.Resilience | Error | Invalid resilience policy
ELRES002 | Elarion.Abstractions.Resilience | Error | Duplicate resilience policy name
ELCACHE001 | Elarion.Abstractions.Caching | Error | Handler cannot be both cacheable and cache-invalidating
ELCACHE002 | Elarion.Abstractions.Caching | Error | Handler cache tags are required
ELCACHE003 | Elarion.Abstractions.Caching | Error | Handler cache tag is invalid
ELCACHE004 | Elarion.Abstractions.Caching | Error | Handler cache duration is invalid
ELAUTH001 | Elarion.Abstractions.Authorization | Error | Authorized handler response cannot represent failure
ELPOL001 | Elarion.Abstractions.Authorization | Error | [AuthorizationPolicy] must be on an IAuthorizationPolicy
ELPOL002 | Elarion.Abstractions.Authorization | Warning | Authorization policy is not in any module
