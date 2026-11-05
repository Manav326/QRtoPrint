const LOCAL_AGENT_PREVIEW_ENDPOINT = "http://127.0.0.1:17842";

/**
 * Native Windows runs on the paired computer itself, so try loopback first.
 * Browser sessions must use only endpoints advertised by the paired Agent.
 */
export function getLocalPreviewEndpointCandidates(
  desktopApp: boolean,
  advertisedEndpoints: readonly string[]
): string[] {
  const candidates = desktopApp
    ? [LOCAL_AGENT_PREVIEW_ENDPOINT, ...advertisedEndpoints]
    : [...advertisedEndpoints];
  return [...new Set(candidates)].slice(0, 8);
}

/**
 * Restrict local preview requests to the Agent's fixed HTTP endpoint.
 * RFC1918 LAN addresses are allowed for browser fallback; loopback is native-only.
 */
export function isPrivateLocalPreviewEndpoint(value: string, allowLoopback = false): URL | null {
  try {
    const endpoint = new URL(value);
    const octets = endpoint.hostname.split(".").map(Number);
    const privateIpv4 = octets.length === 4 &&
      octets.every(part => Number.isInteger(part) && part >= 0 && part <= 255) &&
      (octets[0] === 10 ||
        (octets[0] === 172 && octets[1] >= 16 && octets[1] <= 31) ||
        (octets[0] === 192 && octets[1] === 168));
    const loopbackIpv4 = endpoint.hostname === "127.0.0.1";
    if (endpoint.protocol !== "http:" || endpoint.port !== "17842" ||
        endpoint.username || endpoint.password || endpoint.pathname !== "/" ||
        endpoint.search || endpoint.hash || !(privateIpv4 || (allowLoopback && loopbackIpv4))) return null;
    return endpoint;
  } catch {
    return null;
  }
}
