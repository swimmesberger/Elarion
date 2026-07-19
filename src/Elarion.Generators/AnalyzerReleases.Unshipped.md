### New Rules

 Rule ID    | Category                           | Severity | Notes
------------|------------------------------------|----------|-------------------------------------------------------------------------------
 ELPIPE001  | Elarion.Generators                 | Error    | Decorator AppliesTo predicate must be public
 ELPIPE002  | Elarion.Generators                 | Error    | Decorator AppliesTo predicate has an unsupported signature
 ELPIPE003  | Elarion.Abstractions.Resilience    | Error    | Domain-event consumer cannot be resilient
 ELPIPE004  | Elarion.Abstractions.Resilience    | Warning  | Retrying resilient command handler is not idempotent
 ELSG001    | Elarion.Generators                 | Error    | Hosted service scope must be singleton
 ELSG002    | Elarion.Generators                 | Error    | Invalid explicit service contract
 ELSG003    | Elarion.Generators                 | Error    | Generic services are not supported
 ELSG004    | Elarion.Generators                 | Error    | Invalid scheduled job method signature
 ELSG005    | Elarion.Generators                 | Error    | Generic scheduled job type is not supported
 ELSG006    | Elarion.Generators                 | Error    | Invalid runtime scheduled job type
 ELSG007    | Elarion.Generators                 | Error    | Duplicate scheduled job name
 ELSG008    | Elarion.Generators                 | Error    | Invalid schedule specification
 ELSG009    | Elarion.Generators                 | Error    | Invalid scheduled job concurrency
 ELSG010    | Elarion.Generators                 | Warning  | Scheduled job is not in any module
 ELACT001   | Elarion.Generators                 | Error    | Invalid [Actor] type
 ELACT002   | Elarion.Generators                 | Error    | Invalid actor method
 ELACT003   | Elarion.Generators                 | Warning  | Actor is not in any module
 ELACT004   | Elarion.Generators                 | Error    | Ambiguous actor key
 ELACT005   | Elarion.Generators                 | Error    | Invalid actor constructor
 ELACT006   | Elarion.Generators                 | Warning  | ConfigureAwait(false) inside a [Reentrant] actor
 ELACT008   | Elarion.Generators                 | Error    | Actor event-consumer key cannot be resolved
 ELACT009   | Elarion.Generators                 | Error    | Actor [ConsumeEvent] method must be public
 ELACT010   | Elarion.Generators                 | Error    | Actor [ConsumeEvent] method must take one integration event
 ELACT012   | Elarion.Generators                 | Error    | Invalid actor stream method
 ELACT013   | Elarion.Generators                 | Error    | Virtual-sharded actor must be keyed
 ELEVT001   | Elarion.Generators                 | Error    | Event consumer must be declared on a [Service] class
 ELEVT002   | Elarion.Generators                 | Error    | Invalid event consumer signature
 ELEVT003   | Elarion.Generators                 | Warning  | Event consumer is not in any module
 ELEVT005   | Elarion.Generators                 | Error    | Invalid handler-form event consumer
 ELEVT006   | Elarion.Generators                 | Error    | Event consumers have the same durable identity
 ELMCP003   | Elarion.Mcp                        | Warning  | MCP customization is ignored when the handler excludes MCP
 ELHTTP001  | Elarion.Http                       | Warning  | HTTP endpoint handler has no resolvable request/response shape
 ELHTTP002  | Elarion.Http                       | Warning  | Duplicate HTTP endpoint route
 ELHTTP003  | Elarion.Http                       | Warning  | HTTP endpoint handler is not in any module
 ELHTTP004  | Elarion.Http                       | Warning  | Cannot infer HTTP verb
 ELMOD001   | Elarion.Modules                    | Warning  | Multiple app modules share a namespace
 ELMOD002   | Elarion.Modules                    | Warning  | Cross-module reference to a module-internal type
 ELMOD003   | Elarion.Modules                    | Warning  | Referenced assembly advertises an unsupported Elarion manifest schema version
 ELMOD004   | Elarion.Modules                    | Warning  | [ModuleEndpoints] names an unknown module
 ELMOD005   | Elarion.Modules                    | Warning  | [ModuleEndpoints] class declares no endpoint hook
 ELMOD006   | Elarion.Modules                    | Error    | Duplicate [AppModule] name
 ELAPI001   | Elarion.Modules                    | Error    | Module API interface must be partial
 ELAPI002   | Elarion.Modules                    | Error    | Module API interface must be top-level
 ELAPI003   | Elarion.Modules                    | Warning  | Module API interface is not in any module
 ELAPI004   | Elarion.Modules                    | Error    | Duplicate module API method
 ELRPC001   | Elarion.JsonRpc                    | Warning  | RPC method handler is not in any module
 ELRPC002   | Elarion.JsonRpc                    | Warning  | RPC handler has no resolvable request/response shape
 ELRPC003   | Elarion.JsonRpc                    | Warning  | Duplicate operation name across the handler bus
 ELRES001   | Elarion.Abstractions.Resilience    | Error    | Invalid resilience policy
 ELRES002   | Elarion.Abstractions.Resilience    | Error    | Duplicate resilience policy name
 ELCACHE001 | Elarion.Abstractions.Caching       | Error    | Handler cannot be both cacheable and cache-invalidating
 ELCACHE002 | Elarion.Abstractions.Caching       | Error    | Handler cache tags are required
 ELCACHE003 | Elarion.Abstractions.Caching       | Error    | Handler cache tag is invalid
 ELCACHE004 | Elarion.Abstractions.Caching       | Error    | Handler cache duration is invalid
 ELCACHE005 | Elarion.Abstractions.Caching       | Error    | Event-consumer handler cannot be cacheable
 ELCACHE006 | Elarion.Abstractions.Caching       | Error    | Cache-key property type is not supported
 ELCACHE007 | Elarion.Abstractions.Caching       | Error    | Cache-key property does not exist
 ELAUTH001  | Elarion.Abstractions.Authorization | Error    | Authorized handler response cannot represent failure
 ELAUTH002  | Elarion.Abstractions.Authorization | Error    | RequireResource id path does not resolve
 ELPOL001   | Elarion.Abstractions.Authorization | Error    | [AuthorizationPolicy] must be on an IAuthorizationPolicy
 ELPOL002   | Elarion.Abstractions.Authorization | Warning  | Authorization policy is not in any module
 ELPERM001  | Elarion.Abstractions.Authorization | Warning  | Authorization requirement is not in any module
 ELPERM002  | Elarion.Abstractions.Authorization | Warning  | Permission produces a duplicate typed accessor
 ELFEAT001  | Elarion.Abstractions.Features      | Error    | Feature-gated handler response cannot represent failure
 ELFEAT002  | Elarion.Abstractions.Features      | Warning  | FeatureGate declares no feature name
 ELVAR001   | Elarion.Abstractions.Features      | Error    | Duplicate variant key
 ELVAR003   | Elarion.Abstractions.Features      | Warning  | Variant contract has no default implementation
 ELVAR004   | Elarion.Abstractions.Features      | Error    | Conflicting variant selector
 ELVAR005   | Elarion.Abstractions.Features      | Warning  | Variant service declares a blank feature
 ELVAR006   | Elarion.Abstractions.Features      | Error    | Generic variant implementation is not supported
 ELVAR007   | Elarion.Abstractions.Features      | Error    | Variant implementation must also be a [Service]
 ELVAR008   | Elarion.Abstractions.Features      | Error    | Variant contract mixes selection axes
 ELVAR009   | Elarion.Abstractions.Features      | Warning  | Variant service declares a blank configuration key
 ELVAR010   | Elarion.Abstractions.Features      | Warning  | Variant registry accessor collision
 ELVAL001   | Elarion.Abstractions.Validation    | Error    | Validated handler response cannot represent failure
 ELVAL002   | Elarion.Abstractions.Validation    | Warning  | Validation attributes are not enforced without Elarion.Validation
 ELIDEM001  | Elarion.Abstractions.Idempotency   | Error    | Idempotent handler response cannot represent failure
 ELIDEM002  | Elarion.Abstractions.Idempotency   | Warning  | [Idempotent] handler is not a command
 ELIDEM003  | Elarion.Abstractions.Idempotency   | Error    | [Idempotent] retention is invalid
 ELIDEM004  | Elarion.Abstractions.Idempotency   | Warning  | Handler cannot be both idempotent and cacheable
 ELINBX001  | Elarion.Abstractions.Messaging     | Warning  | [AllowDuplicates] handler is not an integration-event consumer
 ELCEV001   | Elarion.Abstractions.ClientEvents  | Warning  | Client event is not in any module
 ELCEV002   | Elarion.Abstractions.ClientEvents  | Error    | Duplicate client-event topic name
 ELCEV003   | Elarion.Abstractions.ClientEvents  | Warning  | Client events are not registered without Elarion.ClientEvents
 ELREQ001   | Elarion.Abstractions.Requests      | Error    | Self-typed request marker must name the implementing type
 ELREQ002   | Elarion.Abstractions.Requests      | Warning  | Handler response does not match the request's self-typed marker
 ELREQ003   | Elarion.Abstractions.Requests      | Warning  | Stream handler item does not match the request's self-typed marker
