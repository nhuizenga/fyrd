export type SpecStatus =
  | "Draft" | "Queued" | "Researching" | "Building"
  | "FinalReview" | "ReadyForHandoff" | "PrOpened" | "Failed";
export type UnitStage = "Implement" | "Review" | "Validate" | "Done";
export type UnitState = "Running" | "Idle" | "WaitingOnOwner" | "Blocked" | "Done";
export type MemoryScope = "Repo" | "Global";

export interface SpecRun {
  id: string;
  title: string;
  repo: string;
  status: SpecStatus;
  createdAt: string;
  sessionId?: string | null;
  costCents: number;
}

export interface Unit {
  id: string;
  specId: string;
  title: string;
  stage: UnitStage;
  state: UnitState;
}

export interface SpecListEntry {
  run: SpecRun | null;
  id: string;
  error: string | null;
}

export interface CardAnswer {
  text: string;
  remember: boolean;
  scope: MemoryScope;
  answeredAt: string;
}

export interface InboxCard {
  id: string;
  specId: string;
  unitId?: string | null;
  question: string;
  options: string[];
  recommendation?: string | null;
  reason?: string | null;
  details?: string | null;
  allowFreeText: boolean;
  createdAt: string;
  answer?: CardAnswer | null;
}

export interface FyrdConfig {
  squadCap: number;
}

export interface SpecDetail {
  run: SpecRun;
  markdown: string;
  units: Unit[];
}

export interface AnswerBody {
  text: string;
  remember: boolean;
  scope: MemoryScope;
}

export interface NewSpec {
  title: string;
  repo: string;
  markdown: string;
}
