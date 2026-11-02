import { invoke } from "@tauri-apps/api/core";

export interface DesktopAgentStatus {
  serviceReachable: boolean;
  localApiReachable: boolean;
  paired: boolean;
  agentDeviceId: string | null;
  status: string;
  apiBaseUrl: string | null;
}

export interface DesktopLocalNetworkAdapter {
  id: string;
  displayName: string;
  kind: string;
  ipv4Address: string;
}

export interface DesktopLocalNetworkSnapshot {
  networks: DesktopLocalNetworkAdapter[];
  selectedNetworkId: string | null;
  endpoints: string[];
  phoneVerifiedUtc: string | null;
}

export interface DesktopLocalPhoneTest {
  phoneTestUrl: string;
  startedUtc: string;
  expiresUtc: string;
}

export interface DesktopEnrollmentResult {
  success: boolean;
  agentDeviceId: string | null;
  message: string | null;
  code: string | null;
}

export function isDesktopApp(): boolean {
  return typeof window !== "undefined" &&
    "__TAURI_INTERNALS__" in window;
}

export function getLocalAgentStatus(): Promise<DesktopAgentStatus> {
  return invoke<DesktopAgentStatus>("get_local_agent_status");
}

export function getLocalNetworkSnapshot(): Promise<DesktopLocalNetworkSnapshot> {
  return invoke<DesktopLocalNetworkSnapshot>("get_local_network_snapshot");
}

export function selectLocalNetwork(interfaceId: string): Promise<DesktopLocalNetworkSnapshot> {
  return invoke<DesktopLocalNetworkSnapshot>("select_local_network", { interfaceId });
}

export function startLocalPhoneTest(): Promise<DesktopLocalPhoneTest> {
  return invoke<DesktopLocalPhoneTest>("start_local_phone_test");
}

export function enrollLocalAgent(enrollmentCode: string): Promise<DesktopEnrollmentResult> {
  return invoke<DesktopEnrollmentResult>("enroll_local_agent", { enrollmentCode });
}

export function readDesktopSession(): Promise<string | null> {
  return invoke<string | null>("read_secure_session");
}

export function writeDesktopSession(session: string | null): Promise<void> {
  return invoke<void>("write_secure_session", { session });
}