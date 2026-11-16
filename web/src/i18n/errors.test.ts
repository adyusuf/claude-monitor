import { describe, expect, it } from "vitest";
import { dictionaries } from "./index";

/**
 * Not listed on purpose: invalid_os, invalid_arch, invalid_status, invalid_count and invalid_chunk. Only an agent, the device sign-in or another
 * machine-to-API call receives them (AgentEndpoints, RunReports, AskReports, AgentActions, DeviceEndpoints); no page shows them (F-089 reachability check).
 *
 * Codes the API answers to a web page with (Http.Invalid and refusals of the session routes). The page shows `errors.<code>`, so a code
 * without a text would show the raw key. When an endpoint gets a new code, add it here and give it a text in every language.
 */
const webCodes = [
  // sprints (SprintEndpoints, SprintIssueEndpoints, SprintRules, SprintWrites)
  "too_long", "before_start", "invalid_date", "out_of_range", "invalid_list", "not_a_member", "invalid_release", "invalid_application", "archived", "closed",
  "not_planned", "not_active", "dates_required", "active_dates_required", "sprint_active", "invalid_sprint", "invalid_issue", "wrong_scope",
  "limit_reached", "invalid_value", "mirrored_scope", "mirrored_dates_kept", "mirrored_detached", "jira_account_required", "jira_no_board",
  // workspace settings (WorkspaceEndpoints)
  "invalid_mode",
  // answering a permission request (CommandEndpoints)
  "invalid_answers", "invalid_decision", "invalid_duration", "rule_not_allowed",
  // the account menu and the approval-key form (MeEndpoints, ApprovalKeyEndpoints); "invalid" is also the client's fallback for a field error without a code (client.ts, toError)
  "invalid_language", "invalid",
] as const;

describe("the error texts", () => {
  it.each(["en", "tr", "de"] as const)("have a text for every code the API sends to the web in %s", (language) => {
    const errors = dictionaries[language].errors as Record<string, unknown>;
    const missing = webCodes.filter((code) => typeof errors[code] !== "string" || (errors[code] as string).trim() === "");
    expect(missing).toEqual([]);
  });
});
