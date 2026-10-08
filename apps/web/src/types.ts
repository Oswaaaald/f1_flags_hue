// Generated from the .NET API contracts. Run: make contracts
export type State = {
  feed: FeedState;
  runner: RunnerState;
  initializing: boolean;
  startup: StartupState;
  recovery: RecoveryState | null;
  calibration: CalibrationState;
  settings: Settings;
  hue: HueStatus;
  journal: RaceEvent[];
  sessions: RecordedSession[];
  simulation: boolean;
  serverUtc: string;
  version: string;
  uiBuild: string | null;
};
export type Inventory = {
  lights: HueLight[];
  groups: HueGroup[];
  entertainment: HueArea[];
};
export type Scenario = {
  id: string;
  name: string;
  description: string;
};
export type ApiError = {
  code: string;
  error: string;
  traceId: string;
};
export type SelectionRequest = {
  lightIds: string[];
  groupIds: string[];
  entertainmentAreaId: string | null;
};
export type SceneRecallRequest = {
  id: string;
  dynamic: boolean;
};
export type OffsetAdjustmentRequest = {
  deltaSeconds: number;
};
export type SetupRequest = {
  code: string;
  password: string;
};
export type LoginRequest = {
  password: string;
};
export type DesktopLoginRequest = {
  ticket: string;
};
export type PreviewRequest = {
  flag: string;
};
export type ReplayRequest = {
  id: string;
  kind: string;
  speed: number;
};
export type PairRequest = {
  ip: string;
};
export type CalibrationArmRequest = {
  mode: string;
};
export type CalibrationClockRequest = {
  remaining: string;
};
export type SimulationRequest = {
  topic: string;
  payload: unknown;
};
export type RecoveryAbandonRequest = {
  confirmation: string;
};
export type FeedState = {
  connected: boolean;
  sessionKey: string | null;
  sessionName: string | null;
  sessionType: string | null;
  sessionStatus: string | null;
  currentLap: number | null;
  totalLaps: number | null;
  clock: SessionClock | null;
  lastFlag: string | null;
  lastDataAt: string | null;
  lastError: string | null;
  journalError: string | null;
  processingError: string | null;
};
export type RunnerState = {
  running: boolean;
  mode: string | null;
  lastFlag: string | null;
  activeEffect: string | null;
  error: string | null;
  startedAt: string | null;
  stopping: boolean;
  cleanupPending: boolean;
  queuedEvents: number;
  nextEffectAt: string | null;
};
export type StartupState = {
  initializing: boolean;
  phase: string;
  error: string | null;
  retryAt: string | null;
};
export type RecoveryState = {
  pending: boolean;
  bridgeId: string | null;
  lightIds: string[];
  resources: string[];
};
export type CalibrationState = {
  mode: string | null;
  waiting: boolean;
  reference: RaceEvent | null;
  proposedOffset: number | null;
};
export type Settings = {
  schemaVersion: number;
  revision: number;
  brightness: number;
  transitionSeconds: number;
  offsetSeconds: number;
  alertWatchdogSeconds: number;
  restoreOnExit: boolean;
  exitOnChequered: boolean;
  autoLive: boolean;
  lightIds: string[];
  entertainmentAreaId: string | null;
  effects: Record<Flag, Effect>;
};
export type HueStatus = {
  linked: boolean;
  ip: string | null;
  name: string | null;
  entertainment: boolean;
};
export type RaceEvent = {
  kind: string;
  value: string | null;
  sessionKey: string | null;
  sessionName: string | null;
  sessionType: string | null;
  receivedAt: string;
  receivedTicks: number;
  sourceUtc: string | null;
  initial: boolean;
  totalLaps: number | null;
  eventId: string | null;
};
export type RecordedSession = {
  key: string;
  flags: number;
  at: string;
  name: string;
};
export type HueLight = {
  id: string;
  name: string;
  color: boolean;
  legacyId: string | null;
};
export type HueGroup = {
  id: string;
  name: string;
  type: string;
  lightIds: string[];
  legacyId: string | null;
};
export type HueArea = {
  id: string;
  name: string;
  lightIds: string[];
  channels: number[];
};
export type SessionClock = {
  utc: string | null;
  remaining: string | null;
  extrapolating: boolean;
};
export type Flag =
  | "GREEN"
  | "YELLOW"
  | "RED"
  | "SC"
  | "SC_ENDING"
  | "VSC"
  | "VSC_ENDING"
  | "BLUE"
  | "CHEQUERED";
export type Effect = {
  enabled: boolean;
  durationSeconds: number | null;
  mode: string;
  x: number;
  y: number;
};
