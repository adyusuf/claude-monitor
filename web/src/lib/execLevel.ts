/**
 * Turning a machine's remote-run level up or down from its page (ADR-0021). Raising it is signed with the owner's approval key over the exact bytes the API and
 * the machine check (ExecLevelSigning.Payload in the contracts); lowering needs no signature.
 */
export const EXEC_LEVELS = ["off", "argv", "shell"] as const;
export type ExecLevelName = (typeof EXEC_LEVELS)[number];

export const EXEC_LEVEL_TAG = "ap-exec-level-v1";

/** How long a signed change stays valid after it is signed (the API accepts at most 10 minutes). */
export const EXEC_LEVEL_LIFETIME_MS = 5 * 60 * 1000;

const rank = (level: string): number => EXEC_LEVELS.indexOf(level as ExecLevelName);

/** True when going from one level to another widens what may run, so the change must be signed (ExecLevelSigning.Raises). */
export const raises = (from: string | null | undefined, to: string): boolean => rank(to) > Math.max(from ? rank(from) : 0, 0);

/** The bytes signed, as text: the tag, the machine's agent id (lower case), the level, the sequence and the deadline in whole Unix seconds, one per line. */
export const execLevelPayload = (agentId: string, level: string, sequence: number, notAfterIso: string): string =>
  `${EXEC_LEVEL_TAG}\n${agentId.toLowerCase()}\n${level}\n${sequence}\n${Math.floor(Date.parse(notAfterIso) / 1000)}`;
