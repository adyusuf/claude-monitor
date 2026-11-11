import { useCallback, useEffect, useState, type FormEvent } from "react";
import { useNavigate, useParams } from "react-router";
import { api } from "../api/endpoints";
import type { Invitation, Member, Role } from "../api/types";
import { useSession } from "../auth/session";
import { Drawer } from "../components/Drawer";
import { Tabs } from "../components/Tabs";
import { Button, Card, Field, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { date } from "../lib/format";

const ROLES: Role[] = ["owner", "admin", "member", "viewer"];

/** Members and invitations as two tabs. Everyone sees the members; admins see the invitations, invite (from a drawer), change roles and remove. */
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
  const [inviting, setInviting] = useState(false);
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
      setInviting(false);
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
  const alert = notice ? <Notice kind={notice.kind}>{notice.text}</Notice> : null;
  return (
    <div className="page">
      <header className="page-head"><h1>{t("members.title")}</h1></header>
      {inviting ? null : alert}
      <Tabs label={t("views.members.tabs")} tabs={[
        {
          id: "members", label: t("views.members.tabMembers"),
          content: (
            <Card>
              <table className="table">
                <thead>
                  <tr>
                    <th>{t("views.members.colMember")}</th><th>{t("views.members.colRole")}</th><th>{t("views.members.colJoined")}</th><th />
                  </tr>
                </thead>
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
          ),
        },
        {
          id: "invitations", hidden: !admin,
          label: invitations.length > 0 ? `${t("views.members.tabInvitations")} (${invitations.length})` : t("views.members.tabInvitations"),
          content: (
            <Card title={t("members.pending")} actions={<Button onClick={() => setInviting(true)}>{t("members.invite")}</Button>}>
              {invitations.length === 0 ? <p className="muted">{t("members.noPending")}</p> : (
                <table className="table">
                  <thead>
                    <tr>
                      <th>{t("views.members.colInvited")}</th><th>{t("views.members.colRole")}</th><th>{t("views.members.colExpires")}</th><th />
                    </tr>
                  </thead>
                  <tbody>
                    {invitations.map((i) => (
                      <tr key={i.id}>
                        <td>{i.email}</td>
                        <td>{t(`roles.${i.role}`)}</td>
                        <td className="muted small">{date(i.expiresAt)}</td>
                        <td className="right">
                          <button type="button" className="link" onClick={() => void act(() => api.revokeInvitation(ws, i.id))}>{t("session.cancel")}</button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </Card>
          ),
        },
      ]} />
      {inviting ? (
        <Drawer title={t("views.members.inviteTitle")} onClose={() => setInviting(false)}>
          {alert}
          <form className="stack" onSubmit={invite}>
            <Field label={t("members.inviteEmail")} type="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
            <label className="field">
              <span className="field-label">{t("members.role")}</span>
              <select className="input" value={role} onChange={(e) => setRole(e.target.value as Role)}>
                {ROLES.filter((r) => r !== "owner" || myRole === "owner").map((r) => <option key={r} value={r}>{t(`roles.${r}`)}</option>)}
              </select>
            </label>
            <div><Button type="submit">{t("members.invite")}</Button></div>
          </form>
        </Drawer>
      ) : null}
    </div>
  );
}
