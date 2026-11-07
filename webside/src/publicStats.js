export const STATS_REFRESH_MS = 5 * 60 * 1000;
export const STATS_TIMEOUT_MS = 10 * 1000;
export const STATS_STALE_MS = 15 * 60 * 1000;

function timestamp(value) {
  if (typeof value !== 'string') throw new Error('Invalid statistics date');
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,9})?(Z|[+-]\d{2}:\d{2})$/.exec(value);
  if (!match) throw new Error('Invalid statistics date');
  const [, year, month, day, hour, minute, second, zone] = match;
  const leap = +year % 4 === 0 && (+year % 100 !== 0 || +year % 400 === 0);
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  if (+month < 1 || +month > 12 || +day < 1 || +day > days[+month - 1]
    || +hour > 23 || +minute > 59 || +second > 59
    || (zone !== 'Z' && (+zone.slice(1, 3) > 23 || +zone.slice(4) > 59))) {
    throw new Error('Invalid statistics date');
  }
  const result = Date.parse(value);
  if (!Number.isFinite(result)) throw new Error('Invalid statistics date');
  return result;
}

function count(value) {
  if (!Number.isSafeInteger(value) || value < 0) throw new Error('Invalid statistics count');
  return value;
}

export function parsePublicStats(data, now = Date.now()) {
  if (!data || data.schema !== 1 || !data.downloads) throw new Error('Unsupported statistics schema');
  const generatedAt = timestamp(data.generated_at);
  const since = data.since === null ? null : timestamp(data.since);
  if (generatedAt > now + STATS_REFRESH_MS || (since !== null && since > generatedAt)) {
    throw new Error('Invalid statistics date range');
  }
  const downloads = Object.fromEntries(
    ['total', 'stable', 'preview', 'legacy_preview'].map((key) => [key, count(data.downloads[key])]),
  );
  const pageViews = count(data.page_views);
  const visitors = count(data.unique_visitors);
  if (visitors > pageViews || downloads.total !== downloads.stable + downloads.preview + downloads.legacy_preview) {
    throw new Error('Inconsistent statistics counts');
  }
  let history;
  if (data.history !== undefined) {
    if (!data.history || Array.isArray(data.history)
      || !['anchored_existing_output', 'none'].includes(data.history.migration)
      || typeof data.history.unique_visitors_is_lower_bound !== 'boolean') {
      throw new Error('Invalid statistics history');
    }
    history = {
      migration: data.history.migration,
      unique_visitors_is_lower_bound: data.history.unique_visitors_is_lower_bound,
    };
  }
  return {
    schema: 1,
    page_views: pageViews,
    unique_visitors: visitors,
    downloads,
    since: since === null ? null : new Date(since).toISOString(),
    generated_at: new Date(generatedAt).toISOString(),
    ...(history === undefined ? {} : { history }),
  };
}

export function formatStatValue(value, lowerBound = false) {
  if (!Number.isFinite(value)) return '--';
  return `${lowerBound === true ? '\u2265' : ''}${new Intl.NumberFormat('zh-CN').format(value)}`;
}

export const initialStatsState = { stats: null, loading: true, failed: false };

export function statsReducer(state, action) {
  switch (action.type) {
    case 'success':
      // A delayed cache or older response must not replace the latest good snapshot.
      if (state.stats && Date.parse(action.stats.generated_at) < Date.parse(state.stats.generated_at)) {
        return { ...state, loading: false, failed: true };
      }
      return { stats: action.stats, loading: false, failed: false };
    case 'failure':
      return { ...state, loading: false, failed: true };
    default:
      return state;
  }
}

export function getStatsStatus(state, now = Date.now()) {
  if (!state.stats) return state.loading ? 'loading' : 'unavailable';
  if (state.failed || now - Date.parse(state.stats.generated_at) >= STATS_STALE_MS) return 'stale';
  return 'current';
}
