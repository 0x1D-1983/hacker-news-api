# Hacker News Best Stories API

An ASP.NET Core (.NET 10) REST API that returns the best `n` Hacker News stories, ordered by score (highest first).

```http
GET /api/stories/best?n=2
```

```json
[
  {
    "title": "A uBlock Origin update was rejected from the Chrome Web Store",
    "uri": "https://github.com/uBlockOrigin/uBlock-issues/issues/745",
    "postedBy": "ismaildonmez",
    "time": "2019-10-12T13:43:01+00:00",
    "score": 1716,
    "commentCount": 572
  }
]
```

## Running

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project HackerNews.Api --launch-profile http
curl "http://localhost:5097/api/stories/best?n=10"
```

Other endpoints:

| Endpoint | Purpose |
|---|---|
| `GET /api/stories/best?n={1..200}` | Best `n` stories, ordered by descending score |
| `GET /health` | Liveness check (not rate limited, not traced) |
| `GET /openapi/v1.json` | OpenAPI document (Development only) |
| `GET /scalar` | [Scalar](https://scalar.com) interactive API reference (Development only) |

`HackerNews.Api/HackerNews.Api.http` has ready-made requests for VS Code / Rider / Visual Studio.

Tests (no network needed; Hacker News is faked):

```bash
dotnet test HackerNews.slnx
```

### Observability (optional)

Logs go to the console via Serilog, configured in the `Serilog` section of `appsettings.json`. Traces and metrics are exported over OTLP to `Telemetry:OtlpEndpoint` (default `http://localhost:4317`). To see them locally, start the Aspire dashboard and open <http://localhost:18888>:

```bash
docker run --rm -it -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

If no collector is running the exporter fails quietly. Set `Telemetry:Enabled=false` to turn exporting off.

What's emitted:

- **Traces:** incoming ASP.NET Core requests, outgoing `HttpClient` calls to Hacker News, and a custom `RefreshBestStories` span around each cache rebuild.
- **Metrics:** ASP.NET Core / Kestrel, `HttpClient`, .NET runtime, rate limiting (`Microsoft.AspNetCore.RateLimiting`), plus custom `HackerNews.Api` instruments:
  - `hackernews.best_stories.requests`
  - `hackernews.cache.refreshes` (tagged `outcome`)
  - `hackernews.cache.refresh.duration`
  - `hackernews.items.skipped`
- **Logs:** each log line includes the trace id, so it can be correlated with the trace.

## Configuration

All settings live in `HackerNews.Api/appsettings.json` and can be overridden with environment variables (for example `HackerNews__CacheDuration=00:10:00`).

| Setting | Default | Meaning |
|---|---|---|
| `HackerNews:CacheDuration` | `01:00:00` | How long the ranked snapshot is served before Hacker News is queried again |
| `HackerNews:MaxConcurrentRequests` | `8` | Maximum parallel item requests to Hacker News during a refresh |
| `RateLimiting:TokenLimit` | `30` | Burst size per client IP |
| `RateLimiting:TokensPerPeriod` / `ReplenishmentPeriod` | `30` / `00:01:00` | Sustained rate per client IP (30 requests/minute) |
| `Telemetry:Enabled` / `OtlpEndpoint` | `true` / `http://localhost:4317` | OpenTelemetry export |

## Design

The code is split into a few small parts:

- `Controllers/StoriesController` handles HTTP only: it validates `n`, applies the rate-limit policy, and sets `Cache-Control`.
- `Stories/BestStoriesService` builds and caches the ranked snapshot.
- `HackerNews/HackerNewsClient` is a typed `HttpClient` for the Firebase API.
- `RateLimiting/` and `Telemetry/` hold the cross-cutting setup as `IServiceCollection` extensions, so `Program.cs` stays a short composition root.

I used a controller instead of Minimal APIs because validation attributes, response metadata, and the rate-limit attribute keep the endpoint declarative and `Program.cs` uncluttered.

### How load on Hacker News stays bounded

1. **One snapshot for every `n`.** Hacker News returns at most 200 best-story ids. On a cache miss the service fetches all of them, sorts them by score once, and caches that whole ranked list. Any request for `n` is then just a slice of the cached list. So Hacker News receives about 201 requests per `CacheDuration`, no matter how many clients there are or which `n` they ask for.
2. **Stampede protection.** `HybridCache` collapses concurrent misses for the same key into a single refresh. 1,000 simultaneous cold requests still trigger only one rebuild.
3. **Bounded fan-out.** Item requests during a refresh run with at most `MaxConcurrentRequests` in parallel.
4. **Resilience.** The Hacker News client uses `AddStandardResilienceHandler`: retries with exponential backoff and jitter, per-attempt and total timeouts, and a circuit breaker so a struggling upstream isn't hammered.
5. **Cheap cache hits.** The cached snapshot is an immutable type, so `HybridCache` returns the same instance instead of deserializing a copy on every hit. Warm requests take well under a millisecond.

### Protecting this API from abuse

- **Per-client rate limiting:** a token bucket per remote IP (ASP.NET Core rate limiter). Rejected requests get `429` with a `Retry-After` header and a ProblemDetails body, and a warning is logged.
- **Bounded input:** `n` is required and must be between 1 and 200. Anything else returns `400` ValidationProblemDetails.
- **Request timeouts:** a default 30-second server-side request timeout.
- **Client and proxy caching:** responses carry `Cache-Control: public, max-age=60`, so browsers, CDNs, and reverse proxies can absorb repeated traffic.
- **No internals leaked:** errors are returned as ProblemDetails without stack traces. If Hacker News fails, the client gets `503`.

## Assumptions

- **Cache TTL.** The [Hacker News API docs](https://github.com/HackerNews/API) don't define a TTL. They describe the data as "near real time", say "there is currently no rate limit", and the endpoint responds with `Cache-Control: no-cache`. The best-stories list changes slowly, so I chose a 1-hour default (configurable). Scores and comment counts can therefore be up to an hour old.
- **"Best n" means the top `n` by score among the ids from `/v0/beststories`**, not across all of Hacker News. Equal scores keep Hacker News' own order.
- **`n` is capped at 200**, the most ids `beststories` returns.
- **Deleted, dead, or missing (`null`) items are skipped.** In those cases fewer than `n` stories may be returned, and the same applies if Hacker News returns fewer than `n` ids.
- **`uri` is `null` for text posts** (Ask HN and similar) that have no URL. I didn't substitute a link to the Hacker News discussion page.
- **`commentCount` is the item's `descendants`** (total comments, including replies), and `time` is the Unix `time` converted to UTC ISO-8601.
- **A refresh is all-or-nothing.** If any item request still fails after retries, the refresh fails and the caller gets `503`. This avoids caching an incomplete ranking for a whole TTL.
- **Rate limiting uses the TCP remote address.** Forwarded headers are not trusted by default; see the enhancements below.

## Enhancements given more time

- **Alternative design: a hot-loaded cache filled by a Worker service.** The current cache is lazy (read-through): the first request after startup, and the first after each expiry, waits a few seconds while the snapshot is rebuilt (about 5 s for 200 items at 8 concurrent requests). Another option is a `BackgroundService` (in-process, or a separate .NET Worker Service writing to a shared cache such as Redis) that:
  - builds the full 200-story snapshot at startup, before the API reports ready;
  - rebuilds it on a timer (`PeriodicTimer`) somewhat shorter than the TTL;
  - swaps the new snapshot in only after a successful rebuild, so a Hacker News outage keeps the last good snapshot instead of returning `503`.

  Requests would then only ever read memory, and no request would wait for Hacker News. Upstream load becomes a fixed schedule rather than depending on traffic. The trade-offs: the API polls Hacker News even when nobody is calling it, and it needs a readiness check so it doesn't receive traffic before the first load finishes.
- **Incremental updates.** Use Firebase change notifications or `/v0/updates` to refetch only changed items instead of all 200.
- **Distributed cache.** Add Redis as the `HybridCache` L2 so multiple instances share one snapshot and Hacker News load doesn't grow with the instance count. Use a distributed rate limiter (for example Redis-backed) for the same reason.
- **Reverse-proxy awareness.** Behind a load balancer, enable `ForwardedHeaders` with known proxies so rate limiting keys on the real client IP. Optionally add API keys with per-key quotas.
- **Output caching** (`AddOutputCache`, varied by `n`) to skip serialization for hot responses, plus `ETag`/`304` support.
- **Health checks** that report Hacker News reachability and cache age (`/health/ready`), separate from liveness.
- **Hardening:** HTTPS/HSTS in production, Kestrel connection limits, CORS policy if browsers call the API, and a container image / CI pipeline.
- **More tests:** load tests (k6 / NBomber) to confirm throughput and that upstream calls stay flat under load, plus resilience tests with simulated upstream faults.
