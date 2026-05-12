import { useState, useEffect, useCallback, useRef } from 'react';

// Pass a stable (memoized) fetcher. A fresh fetcher identity is treated as a
// new input and triggers an immediate refetch.
export function usePolling<T>(
  fetcher: () => Promise<T>,
  intervalMs: number,
): { data: T | null; loading: boolean; error: Error | null; refresh: () => void } {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<Error | null>(null);
  const fetcherRef = useRef(fetcher);
  const latestRequestIdRef = useRef(0);

  useEffect(() => {
    fetcherRef.current = fetcher;
  }, [fetcher]);

  const doFetch = useCallback(async (markLoading: boolean) => {
    const requestId = ++latestRequestIdRef.current;
    if (markLoading)
      setLoading(true);

    try {
      const result = await fetcherRef.current();
      if (requestId === latestRequestIdRef.current) {
        setData(result);
        setError(null);
      }
    } catch (e) {
      if (requestId === latestRequestIdRef.current) {
        setError(e instanceof Error ? e : new Error(String(e)));
      }
    }

    if (requestId === latestRequestIdRef.current) {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    const timeoutId = window.setTimeout(() => {
      void doFetch(true);
    }, 0);

    return () => window.clearTimeout(timeoutId);
  }, [doFetch, fetcher]);

  useEffect(() => {
    const id = setInterval(() => {
      void doFetch(false);
    }, intervalMs);
    return () => clearInterval(id);
  }, [doFetch, intervalMs]);

  const refresh = useCallback(() => {
    void doFetch(true);
  }, [doFetch]);

  return { data, loading, error, refresh };
}
