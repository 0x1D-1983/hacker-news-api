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

With Docker (the container listens on port 8080):

```bash
docker build -t hackernews-api HackerNews.Api
docker run --rm -p 8080:8080 -e Telemetry__OtlpEndpoint=http://host.docker.internal:4317 hackernews-api
curl "http://localhost:8080/api/stories/best?n=10"
```

Pass `-e Telemetry__Enabled=false` instead if no OTLP collector is running.

### Kubernetes (kind + Skaffold)

Requirements: Docker, [kind](https://kind.sigs.k8s.io/), kubectl and [Skaffold](https://skaffold.dev/) v2 (`brew install kind kubectl skaffold`).

```
k8s/
  kind-cluster.yaml   kind cluster: control plane + "app" worker + tainted "loadtest" worker
  api/api.yaml        API Deployment (probes, resources, non-root), ClusterIP Service, NetworkPolicy
  edge/               edge Envoy proxy
    envoy.yaml        Envoy config: routes /api/* only
    edge.yaml         Envoy Deployment and NodePort Service
  loadtest/           overlay: api + edge wired to a fake Hacker News (WireMock), see Load tests
  k6/                 k6 scripts (load, breakpoint, resilience), packaged as a ConfigMap
  k6.yaml             one-off k6 Job (runs load.js)
  k6-shell.yaml       long-lived k6 pod for running scripts by hand
scripts/cluster-up.sh creates the cluster
skaffold.yaml         build + deploy (profile "loadtest" for the load-test stack)
```

```bash
./scripts/cluster-up.sh   # creates the "hackernews" kind cluster and switches kubectl to kind-hackernews
skaffold run              # one-off build and deploy (skaffold dev: rebuild on change and stream logs)
```

The API and the edge are pinned to the `app` node. The `loadtest` node is tainted so only the load generator and the fake upstream run there, keeping them from competing with the system under test for CPU.

The public entry point is an **edge Envoy proxy** on <http://localhost:8080>. kind publishes the edge NodePort (30080) on the host:

```bash
curl "localhost:8080/api/stories/best?n=10"   # 200
curl -i localhost:8080/health                  # 404 from Envoy: not routed
```

**Only `/api/*` is exposed publicly.** The health endpoints aren't meant to be public, so they're protected at the network layer rather than in the app. Envoy routes `/api` and `/api/*` to the API Service and answers everything else itself with `404`, including `/health*`, `/openapi` and `/scalar`. It normalises paths before routing, so `/api/../health` and `/api/%2e%2e/health` also get `404`. Encoded slashes (`%2F`) are rejected with `400`. The kubelet's startup, readiness and liveness probes call the API pod IP directly, so they bypass the edge and keep working. Readiness gates traffic until the first Hacker News load finishes.

Envoy also sets request-header and idle timeouts, rejects headers with underscores, and retries connection failures and resets. Retrying is safe because the API is read-only, and it keeps API rollouts free of errors. Its admin interface stays on localhost inside the pod.

**Per-client rate limiting works through the edge.** Envoy appends the caller's address to `X-Forwarded-For`, and the edge Service uses `externalTrafficPolicy: Local` so that address isn't replaced by the node's. The API trusts the header only from `TrustedProxies:Networks`, set to kind's pod network (`10.244.0.0/16`). A NetworkPolicy allows only the Envoy pods to connect to the API, so in practice only Envoy is trusted. The app reads just the right-most entry, the one Envoy appended, so a client can't choose its rate-limit bucket by sending its own `X-Forwarded-For`. Kubelet probes and `kubectl port-forward` come from the node rather than a pod, so the policy doesn't affect them. Its config lives in `k8s/edge/envoy.yaml`. Kustomize adds a content hash to the ConfigMap name, so editing the config restarts Envoy on the next deploy.

For developer access to everything, including `/health` and the Scalar UI, go to the API Service directly:

```bash
kubectl port-forward svc/hackernews-api 8081:80   # skaffold dev does this automatically
open http://localhost:8081/scalar/v1
```

Cleanup:

```bash
skaffold delete                  # add -p loadtest if that's what you deployed
kubectl delete pod k6-shell --ignore-not-found
kind delete cluster --name hackernews
```

### Load and resilience tests (k6)

The tests run inside the cluster against the edge (`http://edge`), so they exercise the same path as real clients: Envoy, then the API. The real Hacker News is replaced by **WireMock** (`k8s/loadtest/fake-hackernews.yaml`). It serves 200 story ids and templated items with random scores and realistic lognormal latency (median about 80 ms). Its admin API gives the tests two things: a count of the requests the API sent upstream, and faults injected at runtime.

The `loadtest` overlay changes a few API settings so a short test sees many refresh cycles:

| Setting | Load test | Default |
|---|---|---|
| `HackerNews:RefreshInterval` / `RetryInterval` | 10 s / 5 s | 55 min / 1 min |
| `HackerNews:CacheDuration` | 5 min | 1 h |
| `RateLimiting:TokenLimit` / `TokensPerPeriod` | 1,000,000 per second | 30 per minute |

The rate limit is lifted because every k6 virtual user (VU) comes from one pod IP and would share a single bucket. `CacheDuration` stays longer than the 2-minute simulated outage, so readiness doesn't take the API out of rotation mid-test.

Node budgets (kind doesn't enforce them; they're the container limits on each node):

| Node | Workloads | CPU / memory limits |
|---|---|---|
| `app` | API (500m / 256Mi), edge Envoy (250m / 128Mi) | 0.75 vCPU / 384Mi |
| `loadtest` | k6 (3 CPU / 6Gi), WireMock (1 CPU / 768Mi) | 4 vCPU / 6.75Gi |

Deploy the stack and run `load.js` as a Job:

```bash
skaffold run -p loadtest
kubectl logs -f job/k6
```

Run the other scripts from the k6 shell pod (`kubectl apply -k k8s/k6` first if you edited a script):

```bash
kubectl apply -f k8s/k6-shell.yaml
kubectl exec -it k6-shell -- k6 run resilience.js
kubectl exec -it k6-shell -- env STEP_VUS=100 MAX_VUS=600 HOLD_SECONDS=15 k6 run breakpoint.js
```

| Script | What it does | Passes when |
|---|---|---|
| `load.js` | Ramps to 50 VUs, holds for 2 minutes, random `n` per request | p95 < 500 ms, < 1% errors, ordering and length checks pass, upstream calls stay within the time-based budget |
| `breakpoint.js` | Adds `STEP_VUS` every step up to `MAX_VUS`, aborting at the first step that misses the criteria | Reports the breaking point and the upstream calls for the run |
| `resilience.js` | 20 VUs of steady traffic while a chaos VU applies four 30 s upstream faults in turn: `beststories` returns 503; item requests reset the connection; `beststories` hangs for 20 s; `beststories` returns malformed JSON | Zero client errors and p95 < 500 ms throughout. During each fault the same list keeps being served. Upstream calls during the outage stay within a retry budget. A fresh list appears after recovery |

The upstream budget is `(ceil(seconds / RefreshInterval) + 2) × 201` calls: it depends only on the test's length, never on its traffic. Each script fails if the count from WireMock exceeds it.

Measured on an Apple Silicon laptop (Docker Desktop):

| Run | Client requests | Throughput | p95 | Errors | Hacker News calls |
|---|---|---|---|---|---|
| `load.js` (50 VUs, 3 min) | 437,361 | 2,429 req/s | 93 ms | 0 | 3,015 (budget 4,020): 15 refreshes × 201 |
| `breakpoint.js` (100 → 600 VUs, 2 min) | 376,134 | 3,131 req/s | 302 ms (410 ms at 600 VUs) | 0 | 1,985 (budget 2,814) |
| `resilience.js` (20 VUs, 3.7 min) | 510,952 | 2,271 req/s | 88 ms | 0 | 290 during the 2-minute outage (budget 20,904) |

The number of upstream calls tracks elapsed time, not traffic: 437k client requests cost the same 201 calls per refresh as an idle API would. During the outage the API kept serving the last good list with no client-visible errors. It retried every 5 seconds, not once per request, and picked up a fresh list within one refresh after recovery.

No step up to 600 VUs breached the criteria. Throughput levels off at about 3,100–3,300 req/s while latency grows, and **the bottleneck is the edge Envoy's 250m CPU limit, not the API**: Envoy was throttled in 86% of CFS periods, the API in about 1%. That throttling is also why p95 sits near 90 ms even at low load (CFS enforces limits in 100 ms periods). Serving a request from the hot cache costs the API very little; raise the Envoy limit or add replicas to go further.

Faults are injected one after another. A rebuild that started under one fault may retry successfully once that fault is lifted, which is correct behaviour. So each phase waits 15 seconds (longer than the HTTP client's retries) before pinning the list it expects to stay unchanged.

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
| `TrustedProxies:Networks` | `[]` | CIDR ranges of reverse proxies whose `X-Forwarded-For` is trusted (loopback is always trusted) |
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
- **Rate limiting keys on the client IP.** That is the TCP remote address, unless the request comes from a trusted proxy network (`TrustedProxies:Networks`), in which case it's the right-most `X-Forwarded-For` entry. Clients behind a shared NAT share a bucket.

## Enhancements given more time

- **Separate Worker service with a shared cache.** Move `BestStoriesRefreshWorker` into its own .NET Worker Service that writes the ranked list to Redis, with the API instances only reading it. Hacker News load would then stay at one poller no matter how many API replicas run, and replicas would start ready immediately. A distributed lock or leader election would keep a single poller active. A distributed rate limiter (for example Redis-backed) would belong here too.
- **Snapshot age metric.** Export the list's age as an observable gauge, so alerts fire before readiness flips.
- **Incremental updates.** Use Firebase change notifications or `/v0/updates` to refetch only changed items instead of all 200.
- **Rate limiting at the edge and per API key.** Add Envoy's `local_ratelimit` (or a global rate-limit service) to reject abusive clients before they reach the API, and optionally API keys with per-key quotas. In production, narrow `TrustedProxies:Networks` to a dedicated proxy subnet or node pool if the CNI allows it.
- **Authentication and authorization.** The stories are public data, so the API is anonymous today. Requiring callers to identify themselves would let quotas and rate limits key on the caller instead of the IP, and would let access be revoked per client. I'd put authentication behind a small replaceable interface: one method that issues a token for credentials, one that validates a token into a principal. Start with an in-memory implementation and a few demo users for local runs and tests, then swap in a real identity provider (Entra ID, Keycloak, any OIDC server) using JWT bearer validation, without touching the controllers. Authorization would be ASP.NET Core policies on the principal's claims, for example a scope required for `/api/stories/best` and a higher-quota tier. Envoy could also validate JWTs at the edge (`jwt_authn`), so anonymous traffic never reaches the API.
- **Output caching** (`AddOutputCache`, varied by `n`) to skip serialization for hot responses, plus `ETag`/`304` support.
- **Hardening:** TLS termination at the edge Envoy plus HSTS, Kestrel connection limits, a CORS policy if browsers call the API, a LoadBalancer Service and 2+ Envoy replicas instead of the kind NodePort, and a CI pipeline that builds and pushes the image.
- **Edge sizing.** The load tests show Envoy's 250m CPU limit caps throughput at about 3,200 req/s. Give the edge more CPU (or replicas) before the API, and run the breakpoint test in CI against a fixed-size cluster to catch regressions.
