import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { ApprovalKeyView } from "../api/types";
import { Button, Card, CopyText, Field, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { approvalKeys, type StoredKey } from "../lib/approvalKey";
import { useCodeGate } from "./useCodeGate";

/** The fingerprint in groups of four, which is how a person compares it with what a machine prints. */
export const grouped = (fingerprint: string): string => fingerprint.match(/.{1,4}/g)?.join(" ") ?? fingerprint;

/**
 * The approval keys of the signed-in person (ADR-0010 §4): make a key in this browser (its private half never leaves it), register its public half, and see what to type
 * on a machine whose admin pins it. A machine that requires signed approvals runs nothing this key did not sign.
 */
export function ApprovalKeysCard() {
  const { t } = useI18n();
  const errorText = useErrorText();
  const gate = useCodeGate("reauth_required", "remote.codeHintReauth");
  const [local, setLocal] = useState<StoredKey | null>(null);
  const [keys, setKeys] = useState<ApprovalKeyView[]>([]);
  const [name, setName] = useState("");
  const [problem, setProblem] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setLocal(await approvalKeys.current());
      setKeys(await api.approvalKeys());
    } catch (e) {
      setProblem(errorText(e));
    }
    // errorText changes identity every render
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(() => { void load(); }, [load]);

  const registered = local ? keys.some((k) => k.fingerprint === local.fingerprint && k.retiredAt === null) : false;

  const make = async () => {
    setProblem(null);
    const label = name.trim() || t("approvals.defaultName");
    try {
      const key = local ?? await approvalKeys.generate(label);
      setLocal(key);
      if (await gate.attempt((code) => api.registerApprovalKey(key.name, key.publicKey, code).then(() => undefined))) await load();
    } catch (e) {
      setProblem(errorText(e));
    }
  };

  const retire = async (k: ApprovalKeyView) => {
    setProblem(null);
    try {
      await api.retireApprovalKey(k.id);
      if (local?.fingerprint === k.fingerprint) {
        await approvalKeys.forget();
        setLocal(null);
      }

      await load();
    } catch (e) {
      setProblem(errorText(e));
    }
  };

  return (
    <Card title={t("approvals.title")}>
      <div className="stack">
        <p className="muted small">{t("approvals.intro")}</p>
        {problem ? <Notice kind="error">{problem}</Notice> : null}
        {local ? (
          <div className="stack">
            <p className="small"><strong>{t("approvals.thisBrowser")}</strong> · {local.name} · <code>{grouped(local.fingerprint)}</code> · {registered ? t("approvals.registered") : t("approvals.notRegistered")}</p>
            <p className="muted small">{t("approvals.pinHint")}</p>
            <CopyText text={`aipod approvers add ${local.publicKey} --name "${local.name.replaceAll('"', "")}"`} />
          </div>
        ) : (
          <Field label={t("approvals.name")} value={name} maxLength={64} onChange={(e) => setName(e.target.value)} />
        )}
        {gate.field}
        {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
        {!local || !registered ? <div><Button busy={gate.busy} onClick={() => void make()}>{local ? t("approvals.register") : t("approvals.make")}</Button></div> : null}
        {keys.filter((k) => k.retiredAt === null).length > 0 ? (
          <ul className="plain run-list" aria-label={t("approvals.registeredKeys")}>
            {keys.filter((k) => k.retiredAt === null).map((k) => (
              <li key={k.id} className="grant-row">
                <div className="run-line"><strong>{k.name}</strong><code>{grouped(k.fingerprint)}</code><span className="muted small">{dateTime(k.createdAt)}</span></div>
                <Button variant="ghost" onClick={() => void retire(k)}>{t("approvals.retire")}</Button>
              </li>
            ))}
          </ul>
        ) : null}
      </div>
    </Card>
  );
}
