# ADR-0086: One framework-owned answer for unexpected exceptions, on every transport

- Status: Proposed
- Date: 2026-10-25
- Related: [ADR-0005](0005-cross-module-error-channel.md) (`Result` for expected failures, exceptions for bugs),
  [ADR-0080](0080-errors-are-a-declared-contract-with-a-stable-code.md) (every error carries a stable code).

## Context

An expected failure is an `AppError` in a `Result`; an exception is a bug or an outage. JSON-RPC and MCP caught
exceptions and answered a hard-coded English `"Internal error"`; a generated `[HttpEndpoint]` route let them escape to
the host's exception-handling middleware. An application that wants one localized sentence plus a reference its users
can quote to support therefore needed three mechanisms — a decorator first in its pipeline lists, an
`IExceptionHandler` for REST, and a test keeping the decorator first — and still leaked English: the generator places
the application's decorators *inside* the framework's authorization, validation, audit and observability decorators,
so an exception thrown there never reached the application decorator. Clients then recognised the framework's text by
regular expression, and the application read `Activity.Current` itself so its reference matched the trace.

## Decision

- Each transport owns one **outermost** catch, outside every decorator: the JSON-RPC dispatcher, the MCP tool invoker
  (both the admission gate and the invocation), and the generated `[HttpEndpoint]` delegate, which now wraps handler
  resolution and the call. A request the caller aborted is not answered. Writing the response stays outside the
  catch: a serialization failure of a successful result (a missing `JsonTypeInfo`) still reaches the host.
- Generated HTTP routes can leave an exception to the host: `UnexpectedErrorOptions.LeaveToHost` is the catch's
  filter, so the developer exception page in Development, or an `IExceptionHandler` mapping an exception to its own
  status, keeps working. JSON-RPC and MCP always answer, because a batch entry or a tool call has no host handler.
- The answer is configured once for all transports with `ConfigureElarionUnexpectedErrors(o => …)`:
  `UnexpectedErrorOptions.Message` and `.Reference` are hooks over an `UnexpectedErrorContext` (exception, transport,
  operation, the call's scoped services). Defaults: `"Internal error"` and the current trace id, or a fresh identifier
  when no trace is active. A hook that throws falls back to the default for its part.
- The answer carries the stable code `internal` and the reference **in its own field** — `RpcErrorData.Reference`
  (`error.data.reference`) on JSON-RPC, the tool result's structured content on MCP (when `IncludeErrorDetails` is on,
  the default; a host that turns it off sends the message alone), the `reference` ProblemDetails member on HTTP —
  never inside the message. The transport logs the exception at error level with the reference, and tags the span with
  `elarion.error.reference`.
- The options live in `Elarion.Abstractions` next to `ErrorCodes`, because `Elarion.JsonRpc` depends on Abstractions
  only; the defaults are three lines of BCL code, like `HandlerGates`' resolution.

## Consequences

- The default reference is the trace id, which a client can choose through `traceparent`. It is hex-validated, so it
  cannot inject anything, but two clients can make their references collide; a host that needs unguessable
  references sets the `Reference` hook.

- No application decorator, exception handler or ordering test for this concern; clients branch on `code: "internal"`
  and show `reference` (the generated TypeScript `RpcError.reference`).
- An exception in a generated HTTP endpoint no longer reaches the developer exception page or a host
  `IExceptionHandler`; the log line carries the stack trace and the reference. The JSON-RPC dispatcher no longer
  includes the exception message in `DEBUG` builds of the framework.
- gRPC and the bidirectional connection transport keep their current behaviour; they adopt the options when a
  consumer needs them.

## Rejected alternatives

- **An application decorator placed first.** It cannot sit outside the framework's own decorators, which is exactly
  where the leaked exceptions come from.
- **An ASP.NET `IExceptionHandler` shipped by Elarion.** It covers HTTP only, and every host would have to register it
  for its routes alone.
- **Putting the reference in the message.** Clients would parse text again; a field is the contract.
