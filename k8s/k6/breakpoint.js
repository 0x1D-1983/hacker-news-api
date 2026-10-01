// Steps up the VU count until the load test criteria fail, then aborts and reports the breaking
// point. Each step is judged only on requests made while holding that step's VU count; the ramp
// between steps is excluded. Hacker News calls must stay within the time-based budget throughout.
import exec from 'k6/execution';
import { Gauge } from 'k6/metrics';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.1.0/index.js';
import { criteria, getBestStories, resetUpstreamCount, upstreamBudget, upstreamCount } from './lib.js';

const STEP_VUS = Number(__ENV.STEP_VUS || 50);
const MAX_VUS = Number(__ENV.MAX_VUS || 1000);
const RAMP_SECONDS = Number(__ENV.RAMP_SECONDS || 5);
const HOLD_SECONDS = Number(__ENV.HOLD_SECONDS || 30);
const STEP_SECONDS = RAMP_SECONDS + HOLD_SECONDS;

const levels = [];
for (let vus = STEP_VUS; vus <= MAX_VUS; vus += STEP_VUS) levels.push(vus);

const UPSTREAM_BUDGET = upstreamBudget(levels.length * STEP_SECONDS);
const upstreamRequests = new Gauge('upstream_requests');

const thresholds = { upstream_requests: [`value<=${UPSTREAM_BUDGET}`] };
levels.forEach((_, step) => {
  for (const [metric, threshold] of Object.entries(criteria)) {
    thresholds[`${metric}{step:${step}}`] = [
      { threshold, abortOnFail: true, delayAbortEval: `${RAMP_SECONDS + 5}s` },
    ];
  }
});

export const options = {
  stages: levels.flatMap((vus) => [
    { duration: `${RAMP_SECONDS}s`, target: vus },
    { duration: `${HOLD_SECONDS}s`, target: vus },
  ]),
  thresholds,
  summaryTrendStats: ['avg', 'med', 'p(95)', 'max'],
};

export function setup() {
  resetUpstreamCount();
  return { startedAt: Date.now() };
}

export default function () {
  const elapsed = (Date.now() - exec.scenario.startTime) / 1000;
  const holding = elapsed % STEP_SECONDS >= RAMP_SECONDS;
  exec.vu.metrics.tags.step = holding ? String(Math.floor(elapsed / STEP_SECONDS)) : 'ramp';
  getBestStories();
}

export function teardown({ startedAt }) {
  const count = upstreamCount();
  upstreamRequests.add(count);
  const seconds = Math.round((Date.now() - startedAt) / 1000);
  console.log(`Hacker News calls: ${count} in ${seconds}s (budget ${UPSTREAM_BUDGET}, independent of traffic)`);
}

export function handleSummary(data) {
  const failedSteps = Object.entries(data.metrics)
    .map(([name, metric]) => [name.match(/\{step:(\d+)\}$/), metric])
    .filter(([match, metric]) => match && Object.values(metric.thresholds || {}).some((t) => !t.ok))
    .map(([match]) => Number(match[1]));

  let result;
  if (failedSteps.length === 0) {
    result = `criteria met at every step up to ${MAX_VUS} VUs; raise MAX_VUS to find the breaking point`;
  } else {
    const first = Math.min(...failedSteps);
    const lastPassing = first > 0 ? `${levels[first - 1]} VUs` : `none (failed at the first step)`;
    result = `criteria failed at ${levels[first]} VUs; last passing level: ${lastPassing}`;
  }

  const upstream = data.metrics.upstream_requests;
  const upstreamLine = upstream
    ? `${upstream.values.value} Hacker News calls (budget ${UPSTREAM_BUDGET}) for ${data.metrics.http_reqs.values.count} requests`
    : 'Hacker News calls: not measured';

  return {
    stdout: `${textSummary(data, { indent: ' ', enableColors: true })}\n\n  BREAKING POINT: ${result}\n  UPSTREAM: ${upstreamLine}\n\n`,
  };
}
