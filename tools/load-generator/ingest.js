import http from 'k6/http'
import { check } from 'k6'
import { Counter, Trend } from 'k6/metrics'
import { randomIntBetween } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js'

// Drives POST /api/v1/logs/batch with generated log events.
//
// Batches are the unit of work, so the interesting figure is events/sec, not
// requests/sec: eventsAccepted below is the number that matters.

const eventsSubmitted = new Counter('events_submitted')
const eventsAccepted = new Counter('events_accepted')
const eventsRejected = new Counter('events_rejected')
const batchLatency = new Trend('batch_latency_ms', true)

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5081'
const API_KEY = __ENV.API_KEY || 'iiq_dev_0123456789abcdef'
const BATCH_SIZE = parseInt(__ENV.BATCH_SIZE || '100', 10)

export const options = {
  scenarios: {
    ingest: {
      executor: 'constant-vus',
      vus: parseInt(__ENV.VUS || '10', 10),
      duration: __ENV.DURATION || '30s',
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{expected_response:true}': ['p(95)<500'],
  },
}

const SERVICES = ['payments-api', 'orders-api', 'inventory-api', 'notifications-api']
const ENVIRONMENTS = ['production', 'staging']
const SEVERITIES = ['Error', 'Warning', 'error', 'warn', 'Fatal']

const MESSAGES = [
  'The connection pool has been exhausted, either raise MaxPoolSize (currently 100) or Timeout (currently 15 seconds)',
  'Response status code does not indicate success: 502 (Bad Gateway) from payments-api',
  'Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool.',
  'Unhandled exception while processing order 4471',
]

const EXCEPTIONS = [
  'Npgsql.NpgsqlException',
  'System.Net.Http.HttpRequestException',
  'System.TimeoutException',
]

function uuid() {
  // Client-generated idempotency key: what makes an HTTP retry safe rather
  // than duplicative.
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0
    const v = c === 'x' ? r : (r & 0x3) | 0x8
    return v.toString(16)
  })
}

function buildBatch() {
  const events = []
  for (let i = 0; i < BATCH_SIZE; i++) {
    events.push({
      eventId: uuid(),
      service: SERVICES[randomIntBetween(0, SERVICES.length - 1)],
      environment: ENVIRONMENTS[randomIntBetween(0, ENVIRONMENTS.length - 1)],
      timestamp: new Date().toISOString(),
      severity: SEVERITIES[randomIntBetween(0, SEVERITIES.length - 1)],
      message: MESSAGES[randomIntBetween(0, MESSAGES.length - 1)],
      exceptionType: EXCEPTIONS[randomIntBetween(0, EXCEPTIONS.length - 1)],
      stackTrace: 'at Npgsql.PoolingDataSource.Get(...)\nat Npgsql.NpgsqlConnection.Open(...)',
      traceId: uuid().replace(/-/g, '').slice(0, 32),
      spanId: uuid().replace(/-/g, '').slice(0, 16),
      host: `pod-${randomIntBetween(1, 12)}`,
      metadata: {
        deploymentVersion: '2.31.0',
        region: 'us-east-1',
      },
    })
  }
  return { events }
}

export default function () {
  const batch = buildBatch()

  const response = http.post(`${BASE_URL}/api/v1/logs/batch`, JSON.stringify(batch), {
    headers: {
      'Content-Type': 'application/json',
      'X-Api-Key': API_KEY,
      'X-Correlation-Id': uuid(),
    },
  })

  eventsSubmitted.add(BATCH_SIZE)
  batchLatency.add(response.timings.duration)

  const ok = check(response, {
    'status is 202': (r) => r.status === 202,
  })

  if (ok) {
    const body = response.json()
    eventsAccepted.add(body.accepted)
    eventsRejected.add(body.rejected)
  }
}
