# ADR-0005: Error channel on cross-module contracts (`Result` vs exceptions) and gRPC mapping

- Status: Accepted
- Date: 2026-06-23
- Related: [ADR-0002](0002-cross-module-communication.md) (the `[ModuleContract]` seam and "the gRPC
  future"), [results and errors](../concepts/results-and-errors.mdx),
  [cross-module communication](../concepts/cross-module-communication.mdx).

## Context

[ADR-0002](0002-cross-module-communication.md) made a `[ModuleContract]` interface the **stable seam**
for direct cross-module calls: today an in-process implementation forwards to the owning module's
handlers; later the same interface can be re-implemented by a generated gRPC/HTTP client when the module
is extracted out of process, and consumers never change.

That leaves one design question ADR-0002 did not pin down: **what is a contract method's error
channel?** Elarion handlers already speak `Result<T>` (a success value *or* an `AppError`, see
`src/Elarion.Abstractions/Result.cs` / `AppError.cs`), but a published contract is a deliberate surface
and could equally be exception-based. The choice matters precisely *because* of the extraction promise —
whatever the contract returns must map cleanly to a gRPC error channel **without changing the interface**
when the in-process implementation is swapped for a remote one.

`AppError` is already transport-neutral and structured — a semantic `ErrorKind`
(`Validation`/`NotFound`/`Conflict`/`Forbidden`/`BusinessRule`/`Internal`), a `Message`, and optional
`Data`. The comment on `ErrorKind` is explicit that "the transport layer is responsible for mapping these
to protocol-specific codes." So the taxonomy that has to survive the jump already exists.

## Decision

### 1. Prefer `Result<T>` on the contract surface (default)

A `[ModuleContract]` method should return `Result<T>` unless there is a concrete reason not to:

```csharp
[ModuleContract]
public interface ICustomerLookup {
    ValueTask<Result<Customer>> GetAsync(CustomerId id, CancellationToken ct = default);
}
```

Reasons:

- **Zero impedance with the handler layer.** Handlers return `Result<T>`; the in-process contract
  implementation is a pure value/error map, no `try/catch`.
- **The error union is explicit and total in the signature.** Callers must inspect `IsSuccess` before
  reading `Value`; there is no invisible control flow through exceptions, and the success path stays
  allocation- and throw-free (AOT/trim-friendly).
- **`AppError.Kind` is a structured, transport-neutral taxonomy** that maps onto any wire protocol — it
  is the thing designed to survive extraction.

### 2. Allow an exception-based contract (opt-out of `Result`)

A team may instead publish a contract that returns the bare value and signals failure by throwing — for
example to keep the contract free of the `Elarion.Abstractions` dependency, or to match an idiomatic
exception-based API a consumer expects:

```csharp
[ModuleContract]
public interface ICustomerLookup {
    ValueTask<Customer> GetAsync(CustomerId id, CancellationToken ct = default);  // throws on failure
}
```

`Result<T>` is then **removed from the interface**, and the in-process implementation supplies a small
custom mapping from the handler's `Result` to value-or-throw:

```csharp
[Service]
internal sealed class CustomerLookup(ISalesApi api) : ICustomerLookup {
    public async ValueTask<Customer> GetAsync(CustomerId id, CancellationToken ct = default) {
        var result = await api.GetCustomer(new GetCustomer.Query(id.Value), ct);   // full pipeline -> Result<T>
        if (!result.IsSuccess)
            throw MapToException(result.Error);    // module-owned AppError -> exception mapping
        var r = result.Value;
        return new Customer(r.Id, r.Name);
    }
}
```

The crucial property is that **the interface is identical across the in-process → gRPC transition** in
*both* idioms. Only the implementation's mapping differs (see §3). This is the ADR-0002 promise extended
to the error channel: keep the contract stable, vary the adapter.

### 3. gRPC forward-compatibility — both idioms map cleanly

When the module is extracted, the contract is re-implemented by a gRPC client. Each idiom has a natural,
idiomatic gRPC counterpart, so the interface does not change:

#### 3a. `Result<T>` ⇄ protobuf `oneof` response

A `oneof` response message is the on-the-wire analog of `Result<T>`: exactly one of a success payload or
an error payload. First mirror `AppError` as a transport-neutral message:

```proto
syntax = "proto3";
package sales.v1;

import "google/protobuf/struct.proto";

// Mirror of Elarion.Abstractions.ErrorKind.
enum ErrorKind {
  ERROR_KIND_UNSPECIFIED  = 0;   // proto3 requires a zero default
  ERROR_KIND_VALIDATION   = 1;
  ERROR_KIND_NOT_FOUND    = 2;
  ERROR_KIND_CONFLICT     = 3;
  ERROR_KIND_FORBIDDEN    = 4;
  ERROR_KIND_BUSINESS_RULE = 5;
  ERROR_KIND_INTERNAL     = 6;
}

// Mirror of Elarion.Abstractions.AppError.
message AppError {
  ErrorKind kind = 1;
  string message = 2;
  google.protobuf.Struct data = 3;   // optional structured detail (e.g. validation messages)
}

message Customer { string id = 1; string name = 2; }
message GetCustomerRequest { string id = 1; }

// The Result<Customer> analog: exactly one of value | error.
message GetCustomerResponse {
  oneof result {
    Customer value = 1;
    AppError error = 2;
  }
}

service CustomerLookup {
  rpc Get(GetCustomerRequest) returns (GetCustomerResponse);
}
```

The gRPC client implementation collapses the `oneof` back into `Result<T>` — symmetric with the
in-process forwarder, so `Module B` is unaffected:

```csharp
public async ValueTask<Result<Customer>> GetAsync(CustomerId id, CancellationToken ct = default) {
    var resp = await _client.GetAsync(new GetCustomerRequest { Id = id.Value }, cancellationToken: ct);
    return resp.ResultCase switch {
        GetCustomerResponse.ResultOneofCase.Value => new Customer(resp.Value.Id, resp.Value.Name),
        GetCustomerResponse.ResultOneofCase.Error => MapError(resp.Error),   // proto AppError -> AppError
        _ => AppError.Internal("empty response"),
    };
}
```

This keeps the explicit, total success/error union all the way across the wire — the "elegant mapping"
the contract is reaching for. It is the recommended pairing for a `Result<T>` contract.

#### 3b. Exception contract ⇄ Google Rich Error Model (`google.rpc.Status`)

For the exception-based contract, the idiomatic gRPC analog is the
[Rich Error Model](https://grpc.io/docs/guides/error/): the RPC returns the bare value, and failures
travel as a **non-OK gRPC status** whose details carry a `google.rpc.Status` (typically with an
`ErrorInfo`). The client surfaces them as a thrown `RpcException`, mirroring the in-process `throw`:

```proto
import "google/rpc/status.proto";   // failures ride the status trailer, not the response body

service CustomerLookup {
  rpc Get(GetCustomerRequest) returns (Customer);   // no oneof; bare value on success
}
```

`AppError.Kind` maps onto the canonical `google.rpc.Code` set:

| `ErrorKind` | `google.rpc.Code` | gRPC `StatusCode` |
| --- | --- | --- |
| `Validation` | `INVALID_ARGUMENT` (3) | `InvalidArgument` |
| `NotFound` | `NOT_FOUND` (5) | `NotFound` |
| `Conflict` | `ALREADY_EXISTS` (6) / `ABORTED` (10) | `AlreadyExists` / `Aborted` |
| `Forbidden` | `PERMISSION_DENIED` (7) | `PermissionDenied` |
| `BusinessRule` | `FAILED_PRECONDITION` (9) | `FailedPrecondition` |
| `Internal` | `INTERNAL` (13) | `Internal` |

The gRPC client implementation catches `RpcException`, rebuilds an `AppError` from the status detail, and
re-throws the module's own exception type — so the exception-based contract behaves identically remote or
in-process.

### Recommendation

**Always prefer keeping `Result<T>`** on the contract and pair it with a `oneof` response if/when the
module is extracted. Only drop `Result` for the exception idiom when a concrete need calls for it (a
dependency-free contract, or an exception-native consumer), and then pair it with the Rich Error Model.
The mapping work is the same small adapter either way; the difference is purely whether the error union
is **in the signature** (`Result`/`oneof`) or **out of band** (exception/`google.rpc.Status`).

## Consequences

**Positive**

- The contract's error channel is a deliberate, documented choice with a clean gRPC counterpart for each
  idiom, so the `[ModuleContract]` interface stays stable across the in-process → out-of-process move
  (the ADR-0002 promise, now covering errors).
- `Result<T>` + `oneof` preserves the explicit, total success/error union end to end and keeps the hot
  path throw-free.
- `AppError.Kind` already gives a transport-neutral taxonomy, so the wire mapping is a lookup table, not
  a redesign.

**Negative / accepted**

- Removing `Result<T>` for the exception idiom buys a dependency-free contract at the cost of an explicit
  error signature and a small, module-owned `AppError`→exception (and back) mapping; the default keeps
  `Result` to avoid both.
- The proto `AppError` mirror and the `ErrorKind`→`google.rpc.Code` table must be kept in sync with
  `src/Elarion.Abstractions/AppError.cs` by the module that extracts — the framework ships no generated
  proto today (see ADR-0002 "Deferred follow-ups").
- Two error idioms across contracts is a small consistency cost; the recommendation (prefer `Result`)
  exists to keep most contracts uniform.
