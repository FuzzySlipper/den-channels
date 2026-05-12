import { useEffect } from 'react';

export function useEventSourceRefresh(
  url: string | null,
  eventName: string,
  onRefresh: () => void,
) {
  useEffect(() => {
    if (!url || typeof EventSource === 'undefined') {
      return;
    }

    const source = new EventSource(url);
    let refreshTimer: number | null = null;
    const scheduleRefresh = () => {
      if (refreshTimer !== null) {
        return;
      }

      refreshTimer = window.setTimeout(() => {
        refreshTimer = null;
        onRefresh();
      }, 100);
    };

    source.addEventListener(eventName, scheduleRefresh);

    return () => {
      if (refreshTimer !== null) {
        window.clearTimeout(refreshTimer);
      }
      source.removeEventListener(eventName, scheduleRefresh);
      source.close();
    };
  }, [eventName, onRefresh, url]);
}
