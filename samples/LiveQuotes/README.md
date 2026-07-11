# LiveQuotes — the Elarion realtime middle ground

A simulated market-data feed (~100 ticks/second) streamed through **single-homed in-memory actors**,
conflated to human-readable rates, and pushed to browsers over **SSE client events** — with **zero
database, zero broker, zero external infrastructure**. `dotnet run`, open the page, watch it tick.

This sample exists to make a positioning statement concrete. There is a class of realtime use cases —
live device/machine data, market-style feeds, presence, progress — that sits *between* "just poll a
table" and "operate Kafka": too hot for a database write per update, nowhere near needing
big-company delivery guarantees. Teams of five reinvent this middle ground badly (background threads,
lock-guarded dictionaries, hand-rolled WebSocket hubs). Elarion's answer is the composition you see
here, and every piece is a framework primitive doing what it was built for:

```
MarketFeedService (BackgroundService, owns the "vendor connection")
        │  in-process typed facade calls — deliberately NOT integration events
        ▼
StockQuoteActor per symbol   [Actor(SingleHomed = true)], keyed by symbol
        │  mailbox = first-come-first-served serialization, no locks
        │  sequence guard = the feed's order survives multi-channel delivery
        │  conflation = at most one push per 250 ms per symbol, latest value wins
        ▼
IClientEventPublisher        topic market.quoteChanged, resource-scoped per symbol
        ▼
GET /events (SSE)            browsers subscribe to exactly the symbols they display
GET /quotes, /quotes/{symbol}  the pull path: initial load + reconnect converge
```

## Run it

```bash
dotnet run --project samples/LiveQuotes/LiveQuotes.Api
# → http://localhost:5210
```

## What to look at

- **`StockQuoteActor`** — actor "shape 2" (hot, ephemeral, loss-tolerant): plain fields, no locks, no
  `IActorState` (restart loss is the contract; the feed re-primes in one tick interval), and the
  conflation window that bounds push volume no matter how hot the feed runs.
- **`MarketFeedService`** — the one file a real system would change: replace the random walk with the
  proprietary TCP/vendor client. Note what it does *not* do: publish integration events. The outbox is
  a database write per event — market ticks are neither durable nor business facts.
- **`GetQuote`/`ListQuotes`** — the pull path reads through the facade (mailbox-serialized with the
  feed). Queries and pushes carry the same `Quote` shape.
- **`wwwroot/index.html`** — converge-then-stream: fetch `/quotes`, open one `EventSource` with one
  subscription per symbol, re-fetch on the `elarion.connected` reconnect hint.
- **`LiveQuotes.Tests`** — the whole slice is clock-deterministic and Docker-free: conflation and the
  sequence guard are tested against a `FakeTimeProvider` through the *generated* actor registration.

## Scaling the topology (when one process isn't enough)

The single process you just ran is the honest default. When query traffic outgrows it, split the
roles — the module system makes it configuration, not code:

1. **Worker + web nodes**: run one instance with the Market module enabled (the feed, the actors, the
   `/quotes` endpoints live there) and N web instances with `Modules:Market:Enabled=false`. Route
   `/quotes*` and `/events` to the worker at the ingress.
2. **SSE from every node instead**: add `Elarion.ClientEvents.PostgreSql`
   (`AddElarionPostgreSqlClientEvents`) and browsers can hold their `/events` connection to any web
   node — publishes fan out over `LISTEN/NOTIFY`. Mind the publish volume budget: conflation keeps it
   in range; batch symbols per event if you widen the feed.
3. **Belt and suspenders**: `AddElarionPostgreSqlActorHome<TDbContext>()` (the ADR-0048/0049 role
   lease) turns "two instances accidentally run the feed" into a pointed `ActorNotHomedException`
   instead of a mystery.

And the honest ceiling, straight from the
[actors concept doc](https://elarion.wimmesberger.dev/docs/concepts/actors): if you need thousands of
ticks per second delivered to thousands of concurrent clients, you have left the classical web
application — that is dedicated market-data/streaming infrastructure territory, not a reason to grow
this sample.
