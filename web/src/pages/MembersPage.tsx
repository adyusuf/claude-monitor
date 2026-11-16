import { useCallback, useEffect, useState, type FormEvent } from "react";
import { useNavigate, useParams } from "react-router";
import { api } from "../api/endpoints";
import type { Invitation, Member, Role } from "../api/types";
import { useSession } from "../auth/session";
import { Drawer } from "../components/Drawer";
import { MoreRows } from "../components/MoreRows";
import { Tabs } from "../components/Tabs";
import { Button, Card, Field, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { date } from "../lib/format";
import { nextByCursor, type CursorState } from "../lib/listPages";

const ROLES: Role[] = ["owner", "admin", "member", "viewer"];

type NoticeState = { kind: "error" | "success"; text: string };

/** A list read whole: nothing left to load. */
const WHOLE: CursorState = { truncated: false, cursor: null };

/** Members and invitations as two tabs. Everyone sees the members; admins see the invitations, invite (from a drawer), change roles and remove. */
export function MembersPage() {
  const { ws = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const { me, refresh } = useSession();
  const navigate = useNavigate();
  const [members, setMembers] = useState<Member[] | null>(null);
  const [invitations, setInvitations] = useState<Invitation[]>([]);
  const [membersLeft, setMembersLeft] = useState<CursorState>(WHOLE);
  const [invitationsLeft, setInvitationsLeft] = useState<CursorState>(WHOLE);
  const [loadingMembers, setLoadingMembers] = useState(false);
  const [loadingInvitations, setLoadingInvitations] = useState(false);
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<Role>("member");
  const [inviting, setInviting] = useState(false);
  const [notice, setNotice] = useState<NoticeState | null>(null);
  const myRole = me?.workspaces.find((w) => w.id === ws)?.role;
  const admin = myRole === "owner" || myRole === "admin";

  const load = useCallback(async () => {
    try {
      // The first rows come as the plain list, which says when the API cut it (X-Truncated, F-060); "Load more" reads the rest by cursor.
      const list = await api.memberList(ws);
      setMembers(list.items);
      setMembersLeft({ truncated: list.truncated, cursor: null });
      if (admin) {
        const open = await api.invitationList(ws);
        setInvitations(open.items);
        setInvitationsLeft({ truncated: open.truncated, cursor: null });
      }
    } catch (e) {
      setNotice({ kind: "error", text: errorText(e) });
    }
  }, [ws, admin, errorText]);

  useEffect(() => {
    void load();
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ws, admin]);

  const moreMembers = async () => {
    if (!members) return;
    setLoadingMembers(true);
    try {
      const next = await nextByCursor((cursor) => api.memberPage(ws, cursor), membersLeft, members, (m) => m.userId);
      setMembers(next.rows);
      setMembersLeft(next.state);
    } catch (e) {
      setNotice({ kind: "error", text: errorText(e) });
    } finally {
      setLoadingMembers(false);
    }
  };

  const moreInvitations = async () => {
    setLoadingInvitations(true);
    try {
      const next = await nextByCursor((cursor) => api.invitationPage(ws, cursor), invitationsLeft, invitations, (i) => i.id);
      setInvitations(next.rows);
      setInvitationsLeft(next.state);
    } catch (e) {
      setNotice({ kind: "error", text: errorText(e) });
    } finally {
      setLoadingInvitations(false);
    }
  };

  /** Runs one change and reloads; false when the server refused it (the reason is shown). The notice may depend on what the call answered. */
  const act = async (work: () => Promise<unknown>, success?: string | (() => NoticeState)) => {
    try {
      await work();
    } catch (e) {
      setNotice({ kind: "error", text: errorText(e) });
      return false;
    }
    if (success) setNotice(typeof success === "string" ? { kind: "success", text: success } : success());
    await load();
    return true;
  };

  // An invitation that is saved but whose mail could not go out says so; the open invitations carry the button to send it again.
  const invite = (e: FormEvent) => {
    e.preventDefault();
    let mailSent = true;
    void act(async () => {
      mailSent = (await api.invite(ws, email, role)).mailSent !== false;
      setEmail("");
      setInviting(false);
    }, () => (mailSent ? { kind: "success", text: t("members.invited") } : { kind: "error", text: t("members.mailNotSent") }));
  };

  const resend = (invitationId: string) => {
    let mailSent = true;
    void act(async () => {
      mailSent = (await api.resendInvitation(ws, invitationId)).mailSent;
    }, () => (mailSent ? { kind: "success", text: t("members.resent") } : { kind: "error", text: t("members.mailNotSent") }));
  };

  const leave = async (userId: string) => {
    const self = userId === me?.id;
    if (!window.confirm(self ? t("members.leaveConfirm") : t("members.removeConfirm"))) return;
    const removed = await act(() => api.removeMember(ws, userId));
    if (removed && self) {
      await refresh();
      navigate("/");
    }
  };

  const alert = notice ? <Notice kind={notice.kind}>{notice.text}</Notice> : null;
  // A list that could not be read shows why instead of loading forever.
  if (!members) return alert ? <div className="page"><header className="page-head"><h1>{t("members.title")}</h1></header>{alert}</div> : <Spinner />;
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
                      <td><strong>{m.displayName}</strong>{m.email ? <div className="muted small">{m.email}</div> : null}</td>
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
              {membersLeft.truncated ? <MoreRows note={t("members.truncated", { n: members.length })} busy={loadingMembers} onMore={() => void moreMembers()} /> : null}
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
                          <button type="button" className="link" onClick={() => resend(i.id)}>{t("members.resend")}</button>
                          {" "}
                          <button type="button" className="link" onClick={() => void act(() => api.revokeInvitation(ws, i.id))}>{t("session.cancel")}</button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              {invitationsLeft.truncated ? (
                <MoreRows note={t("members.invitationsTruncated", { n: invitations.length })} busy={loadingInvitations} onMore={() => void moreInvitations()} />
              ) : null}
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
