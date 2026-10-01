import http from 'k6/http';
import { check } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://edge';
const UPSTREAM_URL = __ENV.UPSTREAM_URL || 'http://fake-hackernews:8080';

// Must match HackerNews__RefreshInterval / RetryInterval in k8s/loadtest/kustomization.yaml.
export const REFRESH_SECONDS = Number(__ENV.REFRESH_SECONDS || 10);
export const RETRY_SECONDS = Number(__ENV.RETRY_SECONDS || 5);

const MAX_STORIES = 200;
// One beststories.json call plus one call per story id.
export const CALLS_PER_REFRESH = MAX_STORIES + 1;
// Share of responses whose length and ordering are verified; parsing every one would make k6 the bottleneck.
const VERIFY_SHARE = 0.05;

const ADMIN = { headers: { 'Content-Type': 'application/json' }, tags: { kind: 'admin' } };

export const criteria = {
  http_req_duration: 'p(95)<500',
  http_req_failed: 'rate<0.01',
};

/**
 * Upper bound on Hacker News calls for a test of the given length. It depends only on time:
 * one refresh per REFRESH_SECONDS, plus two cycles of slack for refreshes in flight at either end.
 */
export function upstreamBudget(seconds) {
  return (Math.ceil(seconds / REFRESH_SECONDS) + 2) * CALLS_PER_REFRESH;
}

export function durationSeconds(stages) {
  return stages.reduce((total, stage) => total + parseInt(stage.duration, 10) * (stage.duration.endsWith('m') ? 60 : 1), 0);
}

export function getBestStories(n = 1 + Math.floor(Math.random() * MAX_STORIES), tags = {}) {
  const res = http.get(`${BASE_URL}/api/stories/best?n=${n}`, {
    tags: { name: 'GET /api/stories/best', kind: 'api', ...tags },
  });
  check(res, { 'stories 200': (r) => r.status === 200 });

  if (res.status === 200 && Math.random() < VERIFY_SHARE) {
    const stories = res.json();
    check(stories, {
      'n stories': (s) => s.length === n,
      'ordered by score': (s) => s.every((story, i) => i === 0 || s[i - 1].score >= story.score),
    });
  }

  return res;
}

export function resetUpstreamCount() {
  http.del(`${UPSTREAM_URL}/__admin/requests`, null, ADMIN);
}

/** Hacker News requests the API has made since the last reset. */
export function upstreamCount() {
  const res = http.post(
    `${UPSTREAM_URL}/__admin/requests/count`,
    JSON.stringify({ method: 'GET', urlPathPattern: '/v0/.*' }),
    ADMIN,
  );
  return res.json('count');
}

/** Adds a stub that overrides the healthy one (lower priority number wins). Returns its id. */
export function addFault(request, response) {
  const res = http.post(`${UPSTREAM_URL}/__admin/mappings`, JSON.stringify({ priority: 1, request, response }), ADMIN);
  return res.json('id');
}

export function removeFault(id) {
  http.del(`${UPSTREAM_URL}/__admin/mappings/${id}`, null, ADMIN);
}

/** Restores the healthy stubs from the mounted mapping files. Also clears the request journal. */
export function clearFaults() {
  http.post(`${UPSTREAM_URL}/__admin/mappings/reset`, null, ADMIN);
}
