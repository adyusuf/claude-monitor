import { describe, expect, it } from "vitest";
import { deApprovalRulesErrors } from "./de.approvalRules";
import { enApprovalRulesErrors } from "./en.approvalRules";
import { dictionaries, translate } from "./index";
import { trApprovalRulesErrors } from "./tr.approvalRules";

/**
 * Every error code the permission answer and the approval-rule endpoints return (CommandEndpoints.Answer, ApprovalRuleCreation,
 * RemoteChecks.ReauthRefused, ApprovalEndpoints), plus the status codes the client falls back to: each must read as words, never as the raw key.
 */
const APPROVAL_CODES = [
  "invalid_decision", "too_long", "invalid_answers", "invalid_duration", "rule_not_allowed", "rule_limit", "rule_outside_access",
  "reauth_required", "invalid_code", "account_locked", "not_pending", "not_found", "forbidden", "conflict",
] as const;

describe("the error texts of the approvals", () => {
  it.each(["en", "tr", "de"] as const)("have a translation for every code the answer and the rules return in %s", (language) => {
    for (const code of APPROVAL_CODES) {
      const text = translate(dictionaries[language], `errors.${code}`);
      expect(text, code).not.toBe(`errors.${code}`);
      expect(text.trim(), code).not.toBe("");
    }
  });

  it("reads rule_limit and rule_not_allowed as words in English", () => {
    expect(translate(dictionaries.en, "errors.rule_limit")).toMatch(/standing approvals/);
    expect(translate(dictionaries.en, "errors.rule_not_allowed")).toMatch(/this once/);
  });

  it.each([["en", enApprovalRulesErrors], ["tr", trApprovalRulesErrors], ["de", deApprovalRulesErrors]] as const)(
    "show the text of the approval-rules file in %s, never a copy of the same key written later in the dictionary", (language, rulesErrors) => {
      for (const [code, text] of Object.entries(rulesErrors)) {
        expect(translate(dictionaries[language], `errors.${code}`), code).toBe(text);
      }
    });
});
