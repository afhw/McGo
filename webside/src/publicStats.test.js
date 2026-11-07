import test from 'node:test';
import assert from 'node:assert/strict';
import {
  formatStatValue, getStatsStatus, initialStatsState, parsePublicStats, statsReducer, STATS_STALE_MS,
} from './publicStats.js';

const now = Date.parse('2026-11-07T12:00:00Z');
const fixture = () => ({
  schema: 1,
  page_views: 112,
  unique_visitors: 54,
  downloads: { total: 109, stable: 95, preview: 10, legacy_preview: 4 },
  since: '2026-05-31T22:00:19+08:00',
  generated_at: '2026-11-07T11:59:57.821881+00:00',
});

test('accepts schema 1 with Python microseconds and timezone offsets', () => {
  const stats = parsePublicStats(fixture(), now);
  assert.equal(stats.generated_at, '2026-11-07T11:59:57.821Z');
  assert.equal(stats.since, '2026-05-31T14:00:19.000Z');
  assert.equal(stats.downloads.total, 109);
});

test('accepts zero counts and an unknown start date without inventing values', () => {
  const stats = parsePublicStats({ ...fixture(), since: null, page_views: 0, unique_visitors: 0,
    downloads: { total: 0, stable: 0, preview: 0, legacy_preview: 0 } }, now);
  assert.equal(stats.since, null);
  assert.equal(stats.page_views, 0);
});

test('rejects unsupported schemas, missing fields and nonnumeric or unsafe counts', () => {
  for (const schema of [undefined, 0, 2, '1']) {
    assert.throws(() => parsePublicStats({ ...fixture(), schema }, now));
  }
  for (const value of [undefined, null, '12', -1, 1.5, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1]) {
    for (const key of ['page_views', 'unique_visitors']) {
      assert.throws(() => parsePublicStats({ ...fixture(), [key]: value }, now));
    }
    for (const key of ['total', 'stable', 'preview', 'legacy_preview']) {
      const data = fixture();
      data.downloads[key] = value;
      assert.throws(() => parsePublicStats(data, now));
    }
  }
  for (const data of [null, [], {}, { ...fixture(), downloads: null }]) {
    assert.throws(() => parsePublicStats(data, now));
  }
});

test('rejects impossible, ambiguous, missing, future and reversed dates', () => {
  for (const value of [undefined, '', 0, '2026-11-07', '2026-11-07T11:00:00',
    '2026-02-29T00:00:00Z', '2026-04-31T00:00:00Z', '2026-13-01T00:00:00Z',
    '2026-11-07T24:00:00Z', '2026-11-07T12:60:00Z', '2026-11-07T12:00:00+24:00',
    '2026-11-07T12:00:00+08:60']) {
    for (const key of ['since', 'generated_at']) {
      assert.throws(() => parsePublicStats({ ...fixture(), [key]: value }, now));
    }
  }
  assert.throws(() => parsePublicStats({ ...fixture(), generated_at: null }, now));
  assert.throws(() => parsePublicStats({ ...fixture(), generated_at: '2026-11-07T12:06:00Z' }, now));
  assert.throws(() => parsePublicStats({ ...fixture(), since: '2026-11-07T12:00:00Z' }, now));
  assert.doesNotThrow(() => parsePublicStats({ ...fixture(), since: '2024-02-29T00:00:00Z' }, now));
});

test('rejects inconsistent totals and visitor counts', () => {
  assert.throws(() => parsePublicStats({ ...fixture(), unique_visitors: 113 }, now));
  const data = fixture();
  data.downloads.total = 110;
  assert.throws(() => parsePublicStats(data, now));
});

test('first failure remains unavailable, with no fabricated zero statistics', () => {
  assert.equal(getStatsStatus(initialStatsState, now), 'loading');
  const state = statsReducer(initialStatsState, { type: 'failure' });
  assert.equal(state.stats, null);
  assert.equal(getStatsStatus(state, now), 'unavailable');
});

test('refresh failure retains the last good snapshot and recovery clears failure', () => {
  const stats = parsePublicStats(fixture(), now);
  const loaded = statsReducer(initialStatsState, { type: 'success', stats });
  assert.equal(getStatsStatus(loaded, now), 'current');
  const failed = statsReducer(loaded, { type: 'failure' });
  assert.equal(failed.stats, stats);
  assert.equal(getStatsStatus(failed, now), 'stale');
  const recovered = statsReducer(failed, { type: 'success', stats });
  assert.equal(recovered.failed, false);
  assert.equal(getStatsStatus(recovered, now), 'current');
});

test('older server snapshots cannot replace newer data', () => {
  const stats = parsePublicStats(fixture(), now);
  const loaded = statsReducer(initialStatsState, { type: 'success', stats });
  const older = parsePublicStats({ ...fixture(), generated_at: '2026-11-07T11:00:00Z' }, now);
  const state = statsReducer(loaded, { type: 'success', stats: older });
  assert.equal(state.stats, stats);
  assert.equal(getStatsStatus(state, now), 'stale');
});

test('successful responses still become stale as server data ages', () => {
  const stats = parsePublicStats(fixture(), now);
  const state = statsReducer(initialStatsState, { type: 'success', stats });
  const generated = Date.parse(stats.generated_at);
  assert.equal(getStatsStatus(state, generated + STATS_STALE_MS - 1), 'current');
  assert.equal(getStatsStatus(state, generated + STATS_STALE_MS), 'stale');
});

test('legacy statistics without history retain their existing display', () => {
  const stats = parsePublicStats(fixture(), now);
  assert.equal(Object.hasOwn(stats, 'history'), false);
  assert.equal(formatStatValue(stats.unique_visitors, stats.history?.unique_visitors_is_lower_bound), '54');
});

test('preserves optional migration history and explicit lower-bound semantics', () => {
  for (const migration of ['anchored_existing_output', 'none']) {
    for (const lowerBound of [true, false]) {
      const history = { migration, unique_visitors_is_lower_bound: lowerBound };
      const stats = parsePublicStats({ ...fixture(), history }, now);
      assert.deepEqual(stats.history, history);
      assert.notEqual(stats.history, history);
      assert.equal(formatStatValue(stats.unique_visitors, stats.history.unique_visitors_is_lower_bound), lowerBound ? '\u226554' : '54');
    }
  }
});

test('rejects malformed optional history rather than displaying an uncertain exact count', () => {
  for (const history of [null, [], 'none', {},
    { migration: 'unknown', unique_visitors_is_lower_bound: true },
    { migration: 'none' },
    { unique_visitors_is_lower_bound: true },
    ...['true', 'false', 0, 1, null].map((value) => ({ migration: 'none', unique_visitors_is_lower_bound: value })),
  ]) {
    assert.throws(() => parsePublicStats({ ...fixture(), history }, now), /Invalid statistics history/);
  }
});

test('lower-bound formatting preserves zero, grouped numbers and missing placeholders', () => {
  assert.equal(formatStatValue(0, true), '\u22650');
  assert.equal(formatStatValue(12345, true), '\u226512,345');
  assert.equal(formatStatValue(12345, false), '12,345');
  for (const value of [undefined, null, NaN, Infinity]) {
    assert.equal(formatStatValue(value, true), '--');
  }
});

test('refresh failure retains the historical lower-bound marker', () => {
  const stats = parsePublicStats({ ...fixture(), history: {
    migration: 'anchored_existing_output', unique_visitors_is_lower_bound: true,
  } }, now);
  const loaded = statsReducer(initialStatsState, { type: 'success', stats });
  const failed = statsReducer(loaded, { type: 'failure' });
  assert.equal(failed.stats.history.unique_visitors_is_lower_bound, true);
  assert.equal(formatStatValue(failed.stats.unique_visitors, failed.stats.history.unique_visitors_is_lower_bound), '\u226554');
});
