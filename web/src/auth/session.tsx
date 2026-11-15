import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { Navigate, useLocation } from "react-router";
import { ApiError } from "../api/client";
import { api } from "../api/endpoints";
import type { Me } from "../api/types";
import { Spinner } from "../components/ui";

interface Session {
  me: Me | null;
  loading: boolean;
  refresh: () => Promise<Me | null>;
  signOut: () => Promise<void>;
}

const SessionContext = createContext<Session | null>(null);

/** Who is signed in: /api/me on load and after every sign-in, sign-out or workspace change. */
export function SessionProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async () => {
    try {
      const current = await api.me();
      setMe(current);
      return current;
    } catch (e) {
      if (!(e instanceof ApiError) || e.status !== 401) console.error(e);
      setMe(null);
      return null;
    } finally {
      setLoading(false);
    }
  }, []);

  // Signed out here only once the server has ended the session (or says it is already over): a failed sign-out keeps `me` and throws, so
  // the frame tells the person instead of showing them signed out while the cookie still works.
  const signOut = useCallback(async () => {
    try {
      await api.logout();
    } catch (e) {
      if (!(e instanceof ApiError) || e.status !== 401) throw e;
    }
    setMe(null);
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const value = useMemo(() => ({ me, loading, refresh, signOut }), [me, loading, refresh, signOut]);
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): Session {
  const ctx = useContext(SessionContext);
  if (!ctx) throw new Error("useSession outside SessionProvider");
  return ctx;
}

/** Pages behind sign-in: without a session, to /login and back here afterwards. */
export function RequireAuth({ children }: { children: ReactNode }) {
  const { me, loading } = useSession();
  const location = useLocation();
  if (loading) return <Spinner />;
  if (!me) return <Navigate to={`/login?next=${encodeURIComponent(location.pathname + location.search)}`} replace />;
  return <>{children}</>;
}

/**
 * Only same-site paths are followed after sign-in (no open redirect). Browsers read `\` as `/` and the URL parser strips control
 * characters, so `/\evil` or `/<tab>/evil` would become `//evil`: a path holding either is refused.
 */
export function safeNext(next: string | null): string {
  const unsafe = !next || !next.startsWith("/") || next[1] === "/" || [...next].some((c) => c < " " || c === "\\");
  return unsafe ? "/" : next;
}
