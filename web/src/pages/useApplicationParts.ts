import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { ComponentView, ReleaseView } from "../api/typesProjects";
import { useErrorText } from "../i18n";

interface Parts { forId: string; components: ComponentView[]; releases: ReleaseView[]; error: string | null }
const none: Parts = { forId: "", components: [], releases: [], error: null };

/** The components and releases of one application (empty while none is chosen), for the selects that depend on the application. */
export function useApplicationParts(applicationId: string): Parts {
  const errorText = useErrorText();
  const [parts, setParts] = useState<Parts>(none);

  useEffect(() => {
    if (!applicationId) return;
    let live = true;
    Promise.all([api.components(applicationId), api.releases(applicationId)]).then(
      ([components, releases]) => live && setParts({ forId: applicationId, components, releases, error: null }),
      (e: unknown) => live && setParts({ ...none, forId: applicationId, error: errorText(e) }),
    );
    return () => {
      live = false;
    };
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [applicationId]);

  return parts.forId === applicationId && applicationId ? parts : none;
}
