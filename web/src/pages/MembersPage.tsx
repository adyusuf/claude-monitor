import { useCallback, useEffect, useState, type FormEvent } from "react";
import { useNavigate, useParams } from "react-router";
import { api } from "../api/endpoints";
import type { Invitation, Member, Role } from "../api/types";
import { useSession } from "../auth/session";
import { Button, Card, Field, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { date } from "../lib/format";

const ROLES: Role[] = ["owner", "admin", "member", "viewer"];

/** Members and invitations. Everyone sees the members; admins invite, change roles and remove. */
export function MembersPage() {
  const { ws = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const { me, refresh } = useSession();
  const navigate = useNavigate();
  const [members, setMembers] = useState<Member[] | null>(null);
  const [invitations, setInvitations] = useState<Invitation[]>([]);
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<Role>("member");
  const [notice, setNotice] = useState<{ kind: "error" | "success"; text: string } | null>(null);
  const myRole = me?.workspaces.find((w) => w.id === ws)?.role;
  const admin = myRole === "owner" || myRole === "admin";

  const load = useCallback(async () => {
    try {
      setMembers(await api.members(ws));
      if (admin) setInvitations(await api.invitations(ws));
    } catch (e) {
      setNotice({ kind: "error", text: errorText(e) });
    }
  }, [ws, admin, errorText]);

  useEffect(() => {
    void load();
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ws, admin]);

  const act = async (work: () => Promise<unknown>, success?: string) => {
    try {
      await work();
      if (success) setNotice({ kind: "success", text: success });
      await load();
    } catch (e) {
      setNotice({ kind: "error", text: errorText(e) });
    }
  };

  const invite = (e: FormEvent) => {
    e.preventDefault();
    void act(async () => {
      await api.invite(ws, email, role);
      setEmail("");
    }, t("members.invited"));
  };

  const leave = async (userId: string) => {
    const self = userId === me?.id;
    if (!window.confirm(self ? t("members.leaveConfirm") : t("members.removeConfirm"))) return;
    await act(() => api.removeMember(ws, userId));
    if (self) {
      await refresh();
      navigate("/");
    }
  };

  if (!members) return <Spinner />;
  return (
    <div className="page">
      <header className="page-head"><h1>{t("members.title")}</h1></header>
      {notice ? <Notice kind={notice.kind}>{notice.text}</Notice> : null}
      <Card>
        <table className="table">
          <tbody>
            {members.map((m) => (
              <tr key={m.userId}>
                <td><strong>{m.displayName}</strong><div className="muted small">{m.email}</div></td>
                <td>
                  {admin && m.userId !== me?.id ? (
                    <select className="input" aria-label={t("members.role")} value={m.role}
                      onChange={(e) => void act(() => api.changeRole(ws, m.userId, e.target.value as Role))}>
                      {ROLES.map((r) => <option key={r} value={r}>{t(`roles.${r}`)}</option>)}
                    </select>
                  ) : t(`roles.${m.role}`)}
                </td>
                <td className="muted small">{date(m.joinedAt)}</td>
                <td className="right">
                  {m.userId === me?.id ? <Button variant="ghost" onClick={() => void leave(m.userId)}>{t("members.leave")}</Button>
                    : admin ? <Button variant="ghost" onClick={() => void leave(m.userId)}>{t("members.remove")}</Button> : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </Card>
      {admin ? (
        <>
          <Card title={t("members.invite")}>
            <form className="inline-form" onSubmit={invite}>
              <Field label={t("members.inviteEmail")} type="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
              <label className="field">
                <span className="field-label">{t("members.role")}</span>
                <select className="input" value={role} onChange={(e) => setRole(e.target.value as Role)}>
                  {ROLES.filter((r) => r !== "owner" || myRole === "owner").map((r) => <option key={r} value={r}>{t(`roles.${r}`)}</option>)}
                </select>
              </label>
              <Button type="submit">{t("members.invite")}</Button>
            </form>
          </Card>
          <Card title={t("members.pending")}>
            {invitations.length === 0 ? <p className="muted">{t("members.noPending")}</p> : (
              <ul className="plain">
                {invitations.map((i) => (
                  <li key={i.id} className="spread">
                    <span>{i.email} · {t(`roles.${i.role}`)} · <span className="muted small">{date(i.expiresAt)}</span></span>
                    <button type="button" className="link" onClick={() => void act(() => api.revokeInvitation(ws, i.id))}>{t("session.cancel")}</button>
                  </li>
                ))}
              </ul>
            )}
          </Card>
        </>
      ) : null}
    </div>
  );
}
