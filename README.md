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

To try the endpoint interactively, open the Scalar UI at <http://localhost:5097/scalar/v1#tag/stories/GET/api/stories/best> and use its "Test Request" button.

Other endpoints:

| Endpoint | Purpose |
|---|---|
| `GET /api/stories/best?n={1..200}` | Best `n` stories, ordered by descending score |
| `GET /health` | Liveness: `200` whenever the process is up |
| `GET /health/ready` | Readiness: `200` only when the best stories are loaded and younger than `CacheDuration`, otherwise `503` |
| `GET /openapi/v1.json` | OpenAPI document |
| `GET /scalar` | [Scalar](https://scalar.com) interactive API reference |

Health endpoints are not rate limited, traced, or request-logged. The stories are loaded in the background at startup, which takes about 5 seconds. Until then `/health/ready` and `/api/stories/best` return `503` (the latter with `Retry-After: 5`).

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
| `HackerNews:CacheDuration` | `01:00:00` | TTL: the API reports ready only while the snapshot is younger than this |
| `HackerNews:RefreshInterval` | `00:55:00` | Time between successful rebuilds; must be shorter than `CacheDuration` (checked at startup) |
| `HackerNews:RetryInterval` | `00:01:00` | Time before retrying after a failed rebuild |
| `HackerNews:MaxConcurrentRequests` | `8` | Maximum parallel item requests to Hacker News during a refresh |
| `RateLimiting:TokenLimit` | `30` | Burst size per client IP |
| `RateLimiting:TokensPerPeriod` / `ReplenishmentPeriod` | `30` / `00:01:00` | Sustained rate per client IP (30 requests/minute) |
| `Telemetry:Enabled` / `OtlpEndpoint` | `true` / `http://localhost:4317` | OpenTelemetry export |

## Design

The code is split into a few small parts:

- `Controllers/StoriesController` handles HTTP only: it validates `n`, applies the rate-limit policy, sets `Cache-Control`, and slices the cached list.
- `Stories/BestStoriesLoader` fetches all best stories from Hacker News and ranks them by score.
- `Stories/BestStoriesCache` holds the current ranked snapshot in memory and swaps it atomically.
- `Stories/BestStoriesRefreshWorker` is an in-process `BackgroundService` that keeps the cache hot.
- `Stories/BestStoriesReadinessCheck` backs `/health/ready`.
- `HackerNews/HackerNewsClient` is a typed `HttpClient` for the Firebase API.
- `RateLimiting/` and `Telemetry/` hold the cross-cutting setup as `IServiceCollection` extensions, so `Program.cs` stays a short composition root.

I used a controller instead of Minimal APIs because validation attributes, response metadata, and the rate-limit attribute keep the endpoint declarative and `Program.cs` uncluttered.

### Hot cache

Requests never call Hacker News. The worker owns all upstream traffic:

1. **At startup** it loads all best stories (Hacker News returns at most 200 ids), ranks them by score, and stores the ranked list. Until that finishes, `/health/ready` returns `503`, so an orchestrator such as Kubernetes won't route traffic to the instance.
2. **Every `RefreshInterval`** (55 minutes, slightly shorter than the 1-hour TTL) it rebuilds the list. The data never expires while Hacker News is healthy, and no request ever waits for a rebuild.
3. **The list is replaced only after a successful rebuild.** If Hacker News is down or returns bad data, the last good list keeps being served, the failure is logged, and the rebuild is retried after `RetryInterval`.
4. **Readiness tracks freshness.** Once the last good list is older than `CacheDuration`, `/health/ready` returns `503`, while `/api/stories/best` keeps serving the stale list.

### How load on Hacker News stays bounded

1. **Fixed schedule, independent of traffic.** Hacker News receives about 201 requests (the id list plus 200 items) per `RefreshInterval`, no matter how many clients call the API or which `n` they ask for. Each request just slices the in-memory ranked list, which takes well under a millisecond.
2. **Bounded fan-out.** Item requests during a rebuild run with at most `MaxConcurrentRequests` in parallel.
3. **Resilience.** The Hacker News client uses `AddStandardResilienceHandler`: retries with exponential backoff and jitter, per-attempt and total timeouts, and a circuit breaker so a struggling upstream isn't hammered. Failed rebuilds back off to `RetryInterval` rather than looping.

### Protecting this API from abuse

- **Per-client rate limiting:** a token bucket per remote IP (ASP.NET Core rate limiter). Rejected requests get `429` with a `Retry-After` header and a ProblemDetails body, and a warning is logged.
- **Bounded input:** `n` is required and must be between 1 and 200. Anything else returns `400` ValidationProblemDetails.
- **Request timeouts:** a default 30-second server-side request timeout.
- **Client and proxy caching:** responses carry `Cache-Control: public, max-age=60`, so browsers, CDNs, and reverse proxies can absorb repeated traffic.
- **No internals leaked:** errors are returned as ProblemDetails without stack traces. Hacker News failures never reach clients; they only affect readiness once the data goes stale.

## Assumptions

- **Cache TTL.** The [Hacker News API docs](https://github.com/HackerNews/API) don't define a TTL. They describe the data as "near real time", say "there is currently no rate limit", and the endpoint responds with `Cache-Control: no-cache`. The best-stories list changes slowly, so I chose a 1-hour TTL with a 55-minute refresh (both configurable). Scores and comment counts are normally up to 55 minutes old, and older only while Hacker News is failing.
- **"Best n" means the top `n` by score among the ids from `/v0/beststories`**, not across all of Hacker News. Equal scores keep Hacker News' own order.
- **`n` is capped at 200**, the most ids `beststories` returns.
- **Deleted, dead, or missing (`null`) items are skipped.** In those cases fewer than `n` stories may be returned, and the same applies if Hacker News returns fewer than `n` ids.
- **`uri` is `null` for text posts** (Ask HN and similar) that have no URL. I didn't substitute a link to the Hacker News discussion page.
- **`commentCount` is the item's `descendants`** (total comments, including replies), and `time` is the Unix `time` converted to UTC ISO-8601.
- **A rebuild is all-or-nothing.** If any item request still fails after retries, the whole rebuild is discarded and the previous list stays in place. A partial ranking is never published.
- **Serving stale data beats serving nothing.** During a long Hacker News outage the API keeps answering with the last good list, while readiness reports `503` once that list is older than `CacheDuration`. In Kubernetes, that would take every replica out of rotation at about the same time during a long outage. If you'd rather keep serving, set a larger `CacheDuration` or point the readiness probe at liveness after the first load.
- **Single instance.** Each instance keeps its own in-memory list and polls Hacker News on its own schedule.
- **Rate limiting uses the TCP remote address.** Forwarded headers are not trusted by default; see the enhancements below.

## Enhancements given more time

- **Separate Worker service with a shared cache.** Move `BestStoriesRefreshWorker` into its own .NET Worker Service that writes the ranked list to Redis, with the API instances only reading it. Hacker News load would then stay at one poller no matter how many API replicas run, and replicas would start ready immediately. A distributed lock or leader election would keep a single poller active. A distributed rate limiter (for example Redis-backed) would belong here too.
- **Snapshot age metric.** Export the list's age as an observable gauge, so alerts fire before readiness flips.
- **Incremental updates.** Use Firebase change notifications or `/v0/updates` to refetch only changed items instead of all 200.
- **Reverse-proxy awareness.** Behind a load balancer, enable `ForwardedHeaders` with known proxies so rate limiting keys on the real client IP. Optionally add API keys with per-key quotas.
- **Output caching** (`AddOutputCache`, varied by `n`) to skip serialization for hot responses, plus `ETag`/`304` support.
- **Hardening:** HTTPS/HSTS in production, Kestrel connection limits, CORS policy if browsers call the API, and a container image / CI pipeline.
- **More tests:** load tests (k6 / NBomber) to confirm throughput and that upstream calls stay flat under load, plus resilience tests with simulated upstream faults.
