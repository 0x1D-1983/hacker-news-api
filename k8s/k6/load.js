// Steady load through the edge. Passes when latency and errors meet the criteria and the API's
// Hacker News calls stay within a budget that depends only on time, not on traffic.
import { Gauge } from 'k6/metrics';
import { criteria, durationSeconds, getBestStories, resetUpstreamCount, upstreamBudget, upstreamCount } from './lib.js';

const stages = [
  { duration: '30s', target: 50 },
  { duration: '2m', target: 50 },
  { duration: '30s', target: 0 },
];
const UPSTREAM_BUDGET = upstreamBudget(durationSeconds(stages));

const upstreamRequests = new Gauge('upstream_requests');

export const options = {
  stages,
  thresholds: {
    'http_req_failed{kind:api}': [criteria.http_req_failed],
    'http_req_duration{kind:api}': [criteria.http_req_duration],
    checks: ['rate>0.99'],
    upstream_requests: [`value<=${UPSTREAM_BUDGET}`],
  },
};

export function setup() {
  resetUpstreamCount();
  return { startedAt: Date.now() };
}

export default function () {
  getBestStories();
}

export function teardown({ startedAt }) {
  const count = upstreamCount();
  upstreamRequests.add(count);
  const seconds = Math.round((Date.now() - startedAt) / 1000);
  console.log(`Hacker News calls: ${count} in ${seconds}s (budget ${UPSTREAM_BUDGET}, independent of traffic)`);
}
