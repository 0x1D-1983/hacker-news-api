// Simulated Hacker News outage. While `traffic` keeps steady load on the API, `chaos` injects
// one upstream fault after another through the fake upstream's admin API, then restores it.
// Passes when clients see no errors and normal latency throughout, the last good list is served
// unchanged during the outage, the API keeps retrying without a retry storm, and fresh data
// returns after recovery.
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';
import {
  CALLS_PER_REFRESH,
  REFRESH_SECONDS,
  RETRY_SECONDS,
  addFault,
  clearFaults,
  criteria,
  getBestStories,
  removeFault,
  upstreamCount,
} from './lib.js';

const PHASE_SECONDS = Number(__ENV.PHASE_SECONDS || 30);
// A refresh started in the previous phase keeps retrying (standard resilience: 3 retries with
// exponential backoff from 2s) and may succeed once that fault is lifted, so each phase waits this
// long before pinning the list it expects to stay unchanged.
const GRACE_SECONDS = 15;
// One attempt plus three retries per upstream call.
const HTTP_ATTEMPTS = 4;
const POLL_SECONDS = 2;
const RECOVERY_TIMEOUT_SECONDS = REFRESH_SECONDS + RETRY_SECONDS + 30;
const FINGERPRINT_STORIES = 10;
const TRAFFIC_VUS = Number(__ENV.TRAFFIC_VUS || 20);

const bestStories = { method: 'GET', urlPath: '/v0/beststories.json' };
const anyItem = { method: 'GET', urlPathPattern: '/v0/item/.*' };

const FAULTS = [
  { name: 'beststories returns 503', request: bestStories, response: { status: 503 } },
  { name: 'item requests reset the connection', request: anyItem, response: { fault: 'CONNECTION_RESET_BY_PEER' } },
  { name: 'beststories hangs past the timeouts', request: bestStories, response: { status: 200, jsonBody: [1], fixedDelayMilliseconds: 20000 } },
  { name: 'beststories returns malformed JSON', request: bestStories, response: { status: 200, headers: { 'Content-Type': 'application/json' }, body: '{"not":"a list"' } },
];

const OUTAGE_SECONDS = FAULTS.length * PHASE_SECONDS;
// A failed rebuild is retried after RETRY_SECONDS, not per client request, and costs at most one
// refresh's worth of calls, each retried by the HTTP client. Bounded by time, not by traffic.
const OUTAGE_UPSTREAM_BUDGET = (Math.ceil(OUTAGE_SECONDS / RETRY_SECONDS) + 2) * CALLS_PER_REFRESH * HTTP_ATTEMPTS;
const TOTAL_SECONDS = PHASE_SECONDS + OUTAGE_SECONDS + RECOVERY_TIMEOUT_SECONDS + PHASE_SECONDS;

const outageUpstreamCalls = new Counter('outage_upstream_calls');

export const options = {
  scenarios: {
    traffic: {
      executor: 'constant-vus',
      exec: 'traffic',
      vus: TRAFFIC_VUS,
      duration: `${TOTAL_SECONDS}s`,
    },
    chaos: {
      executor: 'per-vu-iterations',
      exec: 'chaos',
      vus: 1,
      iterations: 1,
      maxDuration: `${TOTAL_SECONDS}s`,
    },
  },
  thresholds: {
    'http_req_failed{scenario:traffic}': ['rate==0'],
    'http_req_duration{scenario:traffic}': [criteria.http_req_duration],
    'checks{scenario:chaos}': ['rate==1'],
    outage_upstream_calls: [`count>0`, `count<=${OUTAGE_UPSTREAM_BUDGET}`],
  },
};

export function traffic() {
  getBestStories();
}

function fingerprint() {
  return getBestStories(FINGERPRINT_STORIES, { probe: 'true' }).body;
}

export function chaos() {
  clearFaults();
  sleep(PHASE_SECONDS);
  check(fingerprint(), { 'healthy before the outage': (body) => body.length > 0 });

  const callsBefore = upstreamCount();
  let stale = null;

  for (const fault of FAULTS) {
    console.log(`Fault: ${fault.name}`);
    const id = addFault(fault.request, fault.response);
    sleep(GRACE_SECONDS);
    stale = fingerprint();

    for (let waited = GRACE_SECONDS; waited < PHASE_SECONDS; waited += POLL_SECONDS) {
      sleep(POLL_SECONDS);
      check(fingerprint(), { 'last good list served during the outage': (body) => body === stale });
    }

    removeFault(id);
  }

  // Read before any mappings reset: WireMock clears the request journal with it.
  outageUpstreamCalls.add(upstreamCount() - callsBefore);
  console.log('Upstream healthy again');

  let recovered = false;
  for (let waited = 0; waited < RECOVERY_TIMEOUT_SECONDS && !recovered; waited += POLL_SECONDS) {
    sleep(POLL_SECONDS);
    recovered = fingerprint() !== stale;
  }
  check(recovered, { 'fresh list after recovery': (value) => value });
}

export function teardown() {
  clearFaults();
}
