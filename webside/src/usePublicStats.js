import { useEffect, useReducer, useState } from 'react';
import {
  getStatsStatus, initialStatsState, parsePublicStats, statsReducer,
  STATS_REFRESH_MS, STATS_TIMEOUT_MS,
} from './publicStats.js';

export function usePublicStats() {
  const [state, dispatch] = useReducer(statsReducer, initialStatsState);
  const [now, setNow] = useState(Date.now);

  useEffect(() => {
    let cancelled = false;
    let controller;
    let refreshTimer;
    const load = async () => {
      controller = new AbortController();
      const timeout = window.setTimeout(() => controller.abort(), STATS_TIMEOUT_MS);
      try {
        const response = await fetch(`/stats.json?ts=${Date.now()}`, {
          cache: 'no-store', signal: controller.signal,
        });
        if (!response.ok) throw new Error(`Stats request failed: ${response.status}`);
        const stats = parsePublicStats(await response.json());
        if (!cancelled) dispatch({ type: 'success', stats });
      } catch {
        if (!cancelled) dispatch({ type: 'failure' });
      } finally {
        window.clearTimeout(timeout);
        if (!cancelled) {
          setNow(Date.now());
          refreshTimer = window.setTimeout(load, STATS_REFRESH_MS);
        }
      }
    };

    load();
    const ageTimer = window.setInterval(() => setNow(Date.now()), 30 * 1000);
    return () => {
      cancelled = true;
      controller?.abort();
      window.clearTimeout(refreshTimer);
      window.clearInterval(ageTimer);
    };
  }, []);

  return { ...state, status: getStatsStatus(state, now) };
}
