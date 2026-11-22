---
type: Architecture Decision Record
title: "ADR-0101: A request that needs the caller's decision answers DecisionRequired"
description: "A new ErrorKind.DecisionRequired, decisions declared with typed options and answered once or per item, one request member that carries the answers, and a generated client helper that asks and resends."
status: stable
---
# ADR-0101: A request that needs the caller's decision answers DecisionRequired

- Status: Accepted (implemented 2026-11-22)
- Date: 2026-11-21
- Related: [ADR-0080](0080-errors-are-a-declared-contract-with-a-stable-code.md) (errors are a declared contract),
  [ADR-0027](0027-declarative-request-validation.md) (validation),
  [ADR-0104](0104-previewable-commands.md) (previewable commands)

## Context

Some commands are valid but need the caller to decide before they run: "this looks like a duplicate — save anyway?",
or a choice between outcomes, such as changing an exported record "only here" or "re-exporting it as a correction".
`ErrorKind` has no kind for that. Applications build each case from parts: an `AppError.Conflict` with a code of its
own and a details payload, a `confirmX` flag or an enum-valued `choice` member on the request, and client code that
recognizes the code, shows a dialog and resends with the flag. One application had about a dozen such flows, each with
its own flag name and dialog wiring, and recently multiple-choice ones. A generic client cannot tell such a conflict
from a real one, so every flow needs its own handling.

The same application later counted 39 files with `Confirm*` request flags (`ConfirmDuplicate`, `ConfirmInvalidRows`,
`ConfirmKeyedAlike`, …) and ten duplicate-rule services, all built from `AppError.Conflict(message, "{area}.{what}",
{What}Details)`, a `Confirm{What}` flag and a client helper that resends with the flag. A bank-statement import added a
third shape: a list of confirmed items (`ConfirmedPossibleDuplicateLines`), where items the caller did not confirm were
skipped silently instead of refused — so a line that became a possible duplicate between preview and commit was held
back without anyone seeing it.

## Decision

- **`ErrorKind.DecisionRequired`**, created with `AppError.DecisionRequired(code, message, data)`. HTTP answers `409`
  (the request conflicts with state until the caller decides), JSON-RPC `-32006`, gRPC `FailedPrecondition`; the kind is
  on the wire, so a client recognizes every decision without knowing its code. The kind is appended to the enum, so the
  numeric values of the existing kinds are unchanged.
- **A decision is declared** on the handler, like an error (ADR-0080):
  `[RequiresDecision("invoice.exported_change", Options = typeof(ExportedChange), PerItem = false, DataType = typeof(…))]`.
  The options are an enum's members; their wire names are the member names in camel case. Without `Options` the
  decision is a confirmation whose one option is `confirm`. The declaration joins the operation's error contract (kind
  `DecisionRequired`), and the schema exports `"decision": { "options": [...], "perItem": true }` next to the code's
  kind and data, which the generated client types. Options that are not an enum are a build error (`ELERR003`).
- **The answers travel in one request member.** A request that can ask implements `IDecidingRequest` with
  `IReadOnlyList<DecisionAnswer>? Decisions`; a `DecisionAnswer` is `{ Code, Item?, Option }` (`Option` defaults to
  `confirm`). `IsConfirmed(code)`, `Decision<TOption>(code)` and their per-item overloads read it, the last answer for a
  code and item winning; an option the enum does not declare counts as no answer, so the handler asks again.
- **Per-item decisions.** `PerItem = true` means the caller answers each item the question lists, by key.
  `Unanswered(code, keys)` gives the items the server found that the caller has not answered; the handler refuses
  while any is unanswered, so an item that appears between two calls is asked about, never skipped silently. Skipping
  an item is an explicit option (`Skip`), not the absence of a confirmation.
- **The generated client asks and resends.** `withDecisions(client, method, params, ask)` runs the call; on a decision
  the method declares it calls `ask(question)` with the code, message, options, `perItem` and typed data, and resends
  with the answer added (one option, or an option per item key), until the call succeeds, fails otherwise, or `ask`
  returns `undefined` (the decision error is then thrown). The dialog stays the application's; the loop and the types
  do not. It is emitted only for a schema that declares a decision.
- **The decision is made inside the transaction**, like any business rule: the handler asks only when the state that
  needs it is there, so a resend after the state changed simply runs.

### Changes from the proposal

- The answers are a list of `{ code, item?, option }` instead of a `code → option` dictionary, so whole-request and
  per-item answers share one shape and one schema.
- `Decide<T>` returning "the option or an error" became `Decision<T>` returning the option or `null`, plus
  `IsConfirmed` and `Unanswered`: the handler builds the question's data itself, so it creates the error itself.
- An unknown option is no answer rather than a validation error: the handler asks again with the options it accepts,
  and no pre-handler check needs the declaration at run time.

## Consequences

- Every "needs your decision" flow shares one shape on the server, the wire and the client; per-flow flags and
  client-side code matching go away.
- Idempotency keys must not span the question and the resend: a resend is a new request with a new key. The client
  helper creates it.
- A breaking addition to `ErrorKind`: exhaustive switches over it in applications get a new case.
- The manifest carries a decision's answer shape in the error's kind field (`DecisionRequired;1|import,skip`), so the
  three-field error encoding other errors use is unchanged across assemblies.
- Migrating a `Confirm{What}` flag is mechanical: the flag becomes a `[RequiresDecision]` confirmation, the
  `Conflict` becomes `DecisionRequired` with the same code and data, and the client helper becomes `withDecisions`.

## Rejected alternatives

- **A `Confirm` flag convention without a new kind.** What applications do now; a generic client still cannot
  recognize the question.
- **`428 Precondition Required`.** It names a missing conditional header, not a missing answer.
- **A two-step protocol (prepare, then commit with a token).** It needs state between the calls and a token store, for
  a question a resend answers.
- **Per-flow boolean members.** They cannot carry a multiple-choice answer, and their names differ per flow.
- **Skipping unconfirmed items.** It is what the bank import did; it makes "not asked yet" and "declined" the same
  answer, so a new item is dropped without anyone seeing it.
- **Validating answers in a pre-handler decorator against the declaration.** It would need the declared options at
  run time on every call for a check the handler's `Decision<T>` already makes.
