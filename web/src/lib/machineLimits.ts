import type { MachineEnvironment, MachineFunction } from "../api/types";

/** The shortest and longest repeat of a schedule in minutes (ScheduleLimits.IntervalMinMinutes, IntervalMaxMinutes). */
export const SCHEDULE_INTERVAL_MIN_MINUTES = 15;
export const SCHEDULE_INTERVAL_MAX_MINUTES = 7 * 24 * 60;

/** The longest first prompt the API takes (AgentActionKinds.ClaudeFirstPromptMax). */
export const FIRST_PROMPT_MAX = 2000;

/** What the agent starts by itself at most per hour (AgentConfig.ClaudeAutoStartsPerHour): said on the page so nobody is surprised by the cost. */
export const AUTO_STARTS_PER_HOUR = 3;

/** The environments Claude may be started in from the web (ADR-0014): the machine's class as an admin set it. */
export const CLAUDE_START_ENVIRONMENTS: readonly MachineEnvironment[] = ["dev", "test"];

/** The environment and the function that make a machine strict (a production or database machine). */
export const STRICT_ENVIRONMENT: MachineEnvironment = "production";
export const STRICT_FUNCTION: MachineFunction = "database";
