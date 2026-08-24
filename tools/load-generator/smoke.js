import http from 'k6/http'
import { check } from 'k6'

// Phase 2 smoke profile: hold a small steady load against the health endpoints
// and assert they stay fast. This is a foundation check, not a load test of the
// ingestion path - that arrives in Phase 3 with the endpoint itself.
export const options = {
  vus: 10,
  duration: '30s',
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<200'],
  },
}

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5080'

export default function () {
  const live = http.get(`${BASE_URL}/health/live`)
  check(live, {
    'liveness is 200': (r) => r.status === 200,
    'liveness reports Healthy': (r) => r.json('status') === 'Healthy',
  })

  const root = http.get(`${BASE_URL}/`)
  check(root, {
    'root is 200': (r) => r.status === 200,
    'root identifies the service': (r) => typeof r.json('service') === 'string',
  })
}
