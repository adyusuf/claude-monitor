import { useEffect, useState } from "react";

/** The current time in milliseconds, redrawn every tickMs, so a countdown or an age on the page stays true. */
export function useNow(tickMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), tickMs);
    return () => clearInterval(id);
  }, [tickMs]);
  return now;
}
