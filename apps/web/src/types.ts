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
export type Settings = {
  effects: Record<Flag, Effect>;
  brightness: number;
  transitionSeconds: number;
  offsetSeconds: number;
  alertWatchdogSeconds: number;
  restoreOnExit: boolean;
  exitOnChequered: boolean;
  autoLive: boolean;
  lightIds: string[];
  entertainmentAreaId: string | null;
};
export type RaceEvent = {
  kind: string;
  value: string;
  receivedAt: string;
  sourceUtc: string | null;
  sessionName: string | null;
};
export type State = {
  feed: {
    connected: boolean;
    sessionName: string | null;
    sessionStatus: string | null;
    sessionType: string | null;
    lastFlag: Flag | null;
    currentLap: number | null;
    totalLaps: number | null;
    lastError: string | null;
    lastDataAt: string | null;
    clock: {
      utc: string | null;
      remaining: string | null;
      extrapolating: boolean;
    } | null;
  };
  runner: {
    running: boolean;
    stopping: boolean;
    cleanupPending: boolean;
    mode: string | null;
    lastFlag: Flag | null;
    activeEffect: Flag | null;
    error: string | null;
  };
  calibration: {
    mode: string | null;
    waiting: boolean;
    reference: RaceEvent | null;
    proposedOffset: number | null;
  };
  settings: Settings;
  hue: {
    linked: boolean;
    ip: string | null;
    name: string | null;
    entertainment: boolean;
  };
  journal: RaceEvent[];
  sessions: { key: string; name: string; flags: number; at: string }[];
  initializing: boolean;
  simulation: boolean;
  version: string;
  serverUtc: string;
};
export type Inventory = {
  lights: { id: string; name: string; color: boolean }[];
  groups: { id: string; name: string; type: string; lightIds: string[] }[];
  entertainment: { id: string; name: string; lightIds: string[] }[];
};
export type Scenario = { id: string; name: string; description: string };
