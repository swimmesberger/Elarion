---
type: Architecture Decision Record
title: "ADR-0101: A request that needs the caller's decision answers DecisionRequired"
description: "A new ErrorKind.DecisionRequired, declared decisions with typed options, one request member that carries the answers, and a generated client helper that asks and resends."
status: draft
---
# ADR-0101: A request that needs the caller's decision answers DecisionRequired

- Status: Proposed
- Date: 2026-11-21
- Related: [ADR-0080](0080-errors-are-a-declared-contract-with-a-stable-code.md) (errors are a declared contract),
  [ADR-0027](0027-declarative-request-validation.md) (validation)

## Context

Some commands are valid but need the caller to decide before they run: "this looks like a duplicate — save anyway?",
or a choice between outcomes, such as changing an exported record "only here" or "re-exporting it as a correction".
`ErrorKind` has no kind for that. Applications build each case from parts: an `AppError.Conflict` with a code of its
own and a details payload, a `confirmX` flag or an enum-valued `choice` member on the request, and client code that
recognizes the code, shows a dialog and resends with the flag. One application had about a dozen such flows, each with
its own flag name and dialog wiring, and recently multiple-choice ones. A generic client cannot tell such a conflict
from a real one, so every flow needs its own handling.

## Decision (proposed)

- **`ErrorKind.DecisionRequired`**, created with `AppError.DecisionRequired(code, message, data)`. HTTP answers `409`
  (the request conflicts with state until the caller decides), JSON-RPC its own error code, and the kind is in the
  wire error, so a client recognizes every decision without knowing its code.
- **A decision is declared** like an error (ADR-0080): `[RequiresDecision("invoice.exported_change", typeof(ExportedChange))]`
  on the handler, where the options are an enum. The schema exports the code with its options, and the generated
  client types them.
- **The answers travel in one request member.** A request that can ask implements `IDecidingRequest` with
  `IReadOnlyDictionary<string, string>? Decisions` (code → chosen option, wire-named). In the handler,
  `request.Decide<ExportedChange>("invoice.exported_change")` returns the chosen option or an `AppError` to return as
  is. Several decisions per request are several keys; an unknown option is a validation error.
- **The generated client asks and resends.** `withDecisions(call, ask)` runs the call; on `DecisionRequired` it calls
  `ask(code, options, data)` (the application's dialog) and resends with the answer added, until the call succeeds,
  fails otherwise, or `ask` cancels. The dialog stays the application's; the loop and the types do not.
- **The decision is made inside the transaction**, like any business rule: the handler asks only when the state that
  needs it is there, so a resend after the state changed simply runs.

## Consequences

- Every "needs your decision" flow shares one shape on the server, the wire and the client; per-flow flags and
  client-side code matching go away.
- Idempotency keys must not span the question and the resend: a resend is a new request with a new key. The client
  helper creates it.
- A breaking addition to `ErrorKind`: exhaustive switches over it in applications get a new case.

## Rejected alternatives

- **A `Confirm` flag convention without a new kind.** What applications do now; a generic client still cannot
  recognize the question.
- **`428 Precondition Required`.** It names a missing conditional header, not a missing answer.
- **A two-step protocol (prepare, then commit with a token).** It needs state between the calls and a token store, for
  a question a resend answers.
- **Per-flow boolean members.** They cannot carry a multiple-choice answer, and their names differ per flow.
