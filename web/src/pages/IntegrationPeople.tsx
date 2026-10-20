import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { PersonView } from "../api/typesIntegrations";
import type { Member } from "../api/types";
import { Button, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";

/** The people of the tool (admins): who is a member, who was invited, who has no visible e-mail. An admin can type an e-mail or link a person to a member. */
export function IntegrationPeople({ id, ws }: { id: string; ws: string }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [people, setPeople] = useState<PersonView[] | null>(null);
  const [members, setMembers] = useState<Member[]>([]);
  const [email, setEmail] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [list, all] = await Promise.all([api.integrationPeople(id), api.members(ws)]);
      setPeople(list);
      setMembers(all);
    } catch (e) {
      setError(errorText(e));
    }
    // errorText is a fresh function each render
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, ws]);

  useEffect(() => {
    void load();
  }, [load]);

  const save = async (person: PersonView, input: { email?: string; userId?: string }) => {
    try {
      await api.setIntegrationPerson(person.id, input);
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  };

  if (!people) return error ? <Notice kind="error">{error}</Notice> : null;
  return (
    <div>
      <p className="muted">{t("integrations.peopleHint")}</p>
      {error ? <Notice kind="error">{error}</Notice> : null}
      <ul className="plain">
        {people.map((p) => (
          <li key={p.id} className="row-input">
            <span className="grow">
              {p.displayName} {p.email ? <span className="muted">{p.email}</span> : <span className="badge warn">{t("integrations.noEmail")}</span>}{" "}
              {p.userName ? <span className="badge ok">{t("integrations.linked", { name: p.userName })}</span> : null}{" "}
              {p.invited ? <span className="badge">{t("integrations.invited")}</span> : null}{" "}
              {p.active ? null : <span className="badge">{t("integrations.inactive")}</span>}
            </span>
            {p.userId === null ? (
              <>
                <input className="input" type="email" aria-label={t("integrations.email")} placeholder={t("integrations.email")} value={email[p.id] ?? ""}
                  onChange={(e) => setEmail({ ...email, [p.id]: e.target.value })} />
                <Button variant="ghost" disabled={!(email[p.id] ?? "").trim()} onClick={() => void save(p, { email: (email[p.id] ?? "").trim() })}>{t("integrations.save")}</Button>
                <select className="input" aria-label={t("integrations.linkMember")} value="" onChange={(e) => e.target.value && void save(p, { userId: e.target.value })}>
                  <option value="">{t("integrations.linkMember")}</option>
                  {members.map((m) => <option key={m.userId} value={m.userId}>{m.displayName}</option>)}
                </select>
              </>
            ) : null}
          </li>
        ))}
      </ul>
    </div>
  );
}
