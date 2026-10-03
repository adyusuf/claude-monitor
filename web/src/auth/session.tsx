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

  const signOut = useCallback(async () => {
    try {
      await api.logout();
    } finally {
      setMe(null);
    }
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

/** Only same-site paths are followed after sign-in (no open redirect). */
export function safeNext(next: string | null): string {
  return next && next.startsWith("/") && !next.startsWith("//") ? next : "/";
}
