import { api } from "../api/endpoints";
import type { WebGrantView } from "../api/types";
import { Button, Notice } from "../components/ui";
import { VisibleText } from "../components/VisibleText";
import { useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { GrantForm } from "./GrantForm";
import { RunCommand } from "./RunCommand";
import { UntrustedReason } from "./RunFacts";
import { useCodeGate } from "./useCodeGate";

/** One grant: its template in full, who holds it, and what the owner (or the grantee, to revoke) can do with it. */
function GrantRow({ grant: g, onChanged }: { grant: WebGrantView; onChanged: () => void }) {
  const { t, tx } = useI18n();
  const gate = useCodeGate("reauth_required", "remote.codeHintReauth");
  const act = async (call: (code?: string) => Promise<void>) => {
    if (await gate.attempt(call)) onChanged();
  };
  return (
    <li className="grant-row">
      <div className="run-line">
        <span className={`badge grant-${g.status}`}>{tx(`remote.grantStatus.${g.status}`)}</span>
        <strong>{g.granteeName}</strong>
        {g.requestedByHostname ? <span className="muted small">{t("remote.askedFrom", { host: g.requestedByHostname })}</span> : null}
        <span className="muted small">{dateTime(g.createdAt)}</span>
      </div>
      <RunCommand run={{ visible: true, mode: "argv", argv: g.template, shellCommand: null }} />
      <dl className="facts">
        <dt>{t("remote.cwd")}</dt>
        <dd><code className="shell-inline"><VisibleText text={g.cwd} /></code></dd>
        <dt>{t("remote.maxTimeout")}</dt>
        <dd>{t("remote.seconds", { n: g.maxTimeoutSeconds })}</dd>
        <dt>{t("remote.validUntil")}</dt>
        <dd>{dateTime(g.expiresAt)} · {t("remote.uses", { n: g.useCount })}{g.lastUsedAt ? ` · ${t("remote.lastUsed", { time: dateTime(g.lastUsedAt) })}` : ""}</dd>
        {g.reason ? <><dt>{t("remote.reason")}</dt><dd><UntrustedReason text={g.reason} /></dd></> : null}
      </dl>
      {gate.field}
      {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
      {g.canDecide || g.canRevoke ? (
        <div className="permission-actions">
          {g.canDecide ? <Button variant="danger" busy={gate.busy} onClick={() => void act(() => api.denyGrant(g.id))}>{t("remote.deny")}</Button> : null}
          {g.canDecide ? <Button busy={gate.busy} onClick={() => void act((code) => api.approveGrant(g.id, code))}>{t("remote.approve")}</Button> : null}
          {g.canRevoke ? <Button variant="ghost" busy={gate.busy} onClick={() => void act(() => api.revokeGrant(g.id))}>{t("remote.revoke")}</Button> : null}
        </div>
      ) : null}
    </li>
  );
}

/** A machine's grants: standing permission for one exact command template. The owner may also give one directly. */
export function GrantList({ grants, agentId, canCreate, onChanged }: {
  grants: WebGrantView[]; agentId: string; canCreate: boolean; onChanged: () => void;
}) {
  const { t } = useI18n();
  return (
    <>
      {grants.length === 0 ? <p className="muted">{t("remote.noGrants")}</p> : (
        <ul className="plain run-list">{grants.map((g) => <GrantRow key={g.id} grant={g} onChanged={onChanged} />)}</ul>
      )}
      {canCreate ? <GrantForm agentId={agentId} onCreated={onChanged} /> : null}
    </>
  );
}
