#!/usr/bin/env node
/**
 * Load test for the Yalla API. Node 20+, no dependencies.
 *
 *   node tests/load/load.mjs --base http://localhost:5610 --vus 50 --duration 60
 *   node tests/load/load.mjs --base ... --scenario tab --qr <token>[,<token>...]
 *
 * Scenarios
 *   public  a guest reading the public page: venues, branch, menu, availability, day slots,
 *           reviews, rooms, search. The paths a diner hits before anything is written.
 *   tab     a guest at a table: scan (open a tab), read the tab, place one order, read
 *           events. Needs the tables' QR tokens (--qr), read from the database of the API
 *           under test.
 *   all     both, half the virtual users each.
 *
 * Every request is timed on its own; the report prints count, errors, requests per second
 * and p50 / p95 / p99 / max per endpoint, and the same over everything. A non-2xx answer
 * counts as an error, with its status in the table, so a 429 or a 409 is visible and not
 * silently averaged in. Exit code 1 when any endpoint's p95 is over --p95 ms (default 500)
 * or its error rate over --errors percent (default 1), so CI can gate on it.
 *
 * Never point it at a database you care about: the tab scenario opens tabs and orders.
 */
import { randomUUID } from 'node:crypto';

const args = parse(process.argv.slice(2));
const BASE = (args.base ?? 'http://localhost:5610').replace(/\/$/, '');
const VUS = Number(args.vus ?? 20);
const DURATION = Number(args.duration ?? 30) * 1000;
const SCENARIO = args.scenario ?? 'public';
const P95_LIMIT = Number(args.p95 ?? 500);
const ERROR_LIMIT = Number(args.errors ?? 1);
const QR = (args.qr ?? '').split(',').filter(Boolean);
const VENUE = args.venue ?? 'yalla-demo';
const BRANCH = args.branch ?? 'yerevan-centre';

function parse(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith('--')) continue;
    const key = a.slice(2);
    const next = argv[i + 1];
    if (next === undefined || next.startsWith('--')) out[key] = true;
    else {
      out[key] = next;
      i++;
    }
  }
  return out;
}

/** @type {Map<string, {times: number[], errors: Map<number|string, number>}>} */
const stats = new Map();

async function timed(name, url, init) {
  const started = performance.now();
  let status;
  let body;
  try {
    const res = await fetch(url, init);
    status = res.status;
    body = res.headers.get('content-type')?.includes('json') ? await res.json() : await res.text();
  } catch (error) {
    status = error?.cause?.code ?? 'ERR';
  }
  const ms = performance.now() - started;
  let s = stats.get(name);
  if (!s) stats.set(name, (s = { times: [], errors: new Map() }));
  s.times.push(ms);
  if (typeof status !== 'number' || status < 200 || status >= 300) {
    s.errors.set(status, (s.errors.get(status) ?? 0) + 1);
    return { ok: false, status, body };
  }
  return { ok: true, status, body };
}

const json = (body, headers = {}) => ({
  method: 'POST',
  headers: { 'content-type': 'application/json', ...headers },
  body: JSON.stringify(body),
});

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// --- discovery ---------------------------------------------------------------------------
async function discover() {
  const branch = await timed('discover', `${BASE}/api/public/branches/${VENUE}/${BRANCH}`);
  if (!branch.ok) throw new Error(`cannot read branch ${VENUE}/${BRANCH}: ${branch.status}`);
  const branchId = branch.body.branchId ?? branch.body.id ?? branch.body.branch?.id;
  if (!branchId) throw new Error(`branch page carries no id: ${JSON.stringify(branch.body).slice(0, 300)}`);
  const menu = await timed('discover', `${BASE}/api/public/branches/${branchId}/menu`);
  const items = [];
  const walk = (o) => {
    if (Array.isArray(o)) o.forEach(walk);
    else if (o && typeof o === 'object') {
      if (o.id && typeof o.priceAmd === 'number' && o.name) items.push(o.id);
      Object.values(o).forEach(walk);
    }
  };
  walk(menu.body);
  stats.delete('discover');
  const today = new Date().toISOString().slice(0, 10);
  return { branchId, items, today };
}

// --- scenarios ---------------------------------------------------------------------------
async function publicUser(ctx, deadline) {
  const { branchId, today } = ctx;
  const b = `${BASE}/api/public/branches`;
  while (performance.now() < deadline) {
    await timed('GET public/venues', `${BASE}/api/public/venues`);
    await timed('GET public/branches/{venue}/{branch}', `${b}/${VENUE}/${BRANCH}`);
    await timed('GET public/branches/{id}/menu', `${b}/${branchId}/menu`);
    await timed('GET public/branches/{id}/availability', `${b}/${branchId}/availability?date=${today}&partySize=2`);
    await timed('GET public/branches/{id}/availability/slots', `${b}/${branchId}/availability/slots?date=${today}&partySize=2`);
    await timed('GET public/branches/{id}/reviews', `${b}/${branchId}/reviews`);
    await timed('GET public/branches/{id}/rooms', `${b}/${branchId}/rooms`);
    await timed('GET public/branches (search)', `${b}?query=cafe`);
  }
}

async function tabUser(ctx, deadline, index) {
  const { items } = ctx;
  if (!QR.length) throw new Error('--qr <token,...> is required for the tab scenario');
  // One phone at one table: it scans once, then reads the tab, orders and reads the event
  // stream for the rest of the sitting. Several phones on one table join the first one's tab.
  const qr = QR[index % QR.length];
  const deviceId = `load-${randomUUID()}`;
  let open;
  while (performance.now() < deadline) {
    open = await timed(
      'POST tabs/open',
      `${BASE}/api/tabs/open`,
      json({ qrToken: qr, deviceId, clientCommandId: randomUUID(), displayName: `Load ${index}` }),
    );
    if (open.ok) break;
    await sleep(200);
  }
  if (!open?.ok) return;
  const token = open.body.token?.accessToken;
  const tabId = open.body.token?.tabId ?? open.body.tab?.tabId;
  const auth = { authorization: `Bearer ${token}` };
  let n = 0;
  while (performance.now() < deadline) {
    await timed('GET tabs/{id}', `${BASE}/api/tabs/${tabId}`, { headers: auth });
    if (items.length && n++ % 3 === 0) {
      await timed(
        'POST tabs/{id}/orders',
        `${BASE}/api/tabs/${tabId}/orders`,
        json({ items: [{ menuItemId: items[n % items.length], quantity: 1 }], clientCommandId: randomUUID() }, auth),
      );
    }
    await timed('GET tabs/{id}/events', `${BASE}/api/tabs/${tabId}/events`, { headers: auth });
    await timed('GET tabs/{id}/shares', `${BASE}/api/tabs/${tabId}/shares`, { headers: auth });
  }
}

// --- report ------------------------------------------------------------------------------
function pct(sorted, p) {
  if (!sorted.length) return 0;
  return sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)];
}

function row(name, times, errors, elapsedMs) {
  const sorted = [...times].sort((a, b) => a - b);
  const errorCount = [...errors.values()].reduce((a, b) => a + b, 0);
  const errorRate = times.length ? (100 * errorCount) / times.length : 0;
  const p95 = pct(sorted, 95);
  return {
    bad: p95 > P95_LIMIT || errorRate > ERROR_LIMIT,
    data: {
      endpoint: name,
      n: times.length,
      rps: +(times.length / (elapsedMs / 1000)).toFixed(1),
      'err%': +errorRate.toFixed(2),
      statuses: [...errors.entries()].map(([k, v]) => `${k}x${v}`).join(' '),
      p50: Math.round(pct(sorted, 50)),
      p95: Math.round(p95),
      p99: Math.round(pct(sorted, 99)),
      max: Math.round(sorted.at(-1) ?? 0),
    },
  };
}

function report(elapsedMs) {
  const rows = [];
  let failed = false;
  const all = [];
  const allErrors = new Map();
  for (const [name, s] of [...stats.entries()].sort()) {
    const r = row(name, s.times, s.errors, elapsedMs);
    if (r.bad) failed = true;
    rows.push({ ...r.data, gate: r.bad ? 'FAIL' : 'ok' });
    all.push(...s.times);
    for (const [k, v] of s.errors) allErrors.set(k, (allErrors.get(k) ?? 0) + v);
  }
  rows.push({ ...row('ALL', all, allErrors, elapsedMs).data, gate: '' });
  console.log(`\n${SCENARIO} · ${VUS} virtual users · ${Math.round(elapsedMs / 1000)} s · ${BASE}`);
  console.table(rows);
  console.log(`gate: p95 <= ${P95_LIMIT} ms and errors <= ${ERROR_LIMIT}% per endpoint -> ${failed ? 'FAIL' : 'PASS'}`);
  return failed;
}

// --- main --------------------------------------------------------------------------------
const ctx = await discover();
console.log(`branch ${ctx.branchId}, ${ctx.items.length} menu items, ${QR.length} QR tokens`);
const started = performance.now();
const deadline = started + DURATION;
const users = [];
for (let i = 0; i < VUS; i++) {
  const kind = SCENARIO === 'all' ? (i % 2 ? 'tab' : 'public') : SCENARIO;
  users.push(kind === 'tab' ? tabUser(ctx, deadline, i) : publicUser(ctx, deadline));
}
await Promise.all(users);
process.exit(report(performance.now() - started) ? 1 : 0);
