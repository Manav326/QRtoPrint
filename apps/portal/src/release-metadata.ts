import { useEffect, useState } from "react";

export type PublicRelease = {
  tag_name: string;
  html_url: string;
  assets: Array<{ name: string; browser_download_url: string }>;
};

export type PublicReleaseState = "loading" | "ready" | "error";

const releasesApiUrl = import.meta.env.VITE_SMARTPRINT_RELEASES_API_URL?.trim() ?? "";
const releasesPageUrl = import.meta.env.VITE_SMARTPRINT_RELEASES_URL?.trim() ?? "";

export function getReleasePageUrl(): string {
  return releasesPageUrl;
}

function isPublicRelease(value: unknown): value is PublicRelease {
  if (typeof value !== "object" || value === null) return false;
  const candidate = value as Partial<PublicRelease>;
  return typeof candidate.tag_name === "string" &&
    typeof candidate.html_url === "string" &&
    Array.isArray(candidate.assets) &&
    candidate.assets.every(asset =>
      typeof asset === "object" && asset !== null &&
      typeof asset.name === "string" &&
      typeof asset.browser_download_url === "string"
    );
}

export function getReleaseAssetUrl(release: PublicRelease | null, assetName: string): string | null {
  const asset = release?.assets.find(candidate => candidate.name === assetName);
  if (!asset?.browser_download_url || !releasesPageUrl) return null;

  try {
    const releasesPage = new URL(releasesPageUrl);
    const download = new URL(asset.browser_download_url);
    if (releasesPage.protocol !== "https:" || download.protocol !== "https:" ||
        download.origin !== releasesPage.origin) return null;
    return download.toString();
  } catch {
    return null;
  }
}

export function usePublicRelease(): {
  release: PublicRelease | null;
  state: PublicReleaseState;
  releasesPageUrl: string;
} {
  const [release, setRelease] = useState<PublicRelease | null>(null);
  const [state, setState] = useState<PublicReleaseState>("loading");

  useEffect(() => {
    if (!releasesApiUrl || !releasesPageUrl) {
      setState("error");
      return;
    }

    const controller = new AbortController();
    const loadLatestRelease = async () => {
      try {
        const api = new URL(releasesApiUrl);
        const page = new URL(releasesPageUrl);
        if (api.protocol !== "https:" || page.protocol !== "https:") {
          throw new Error("Release metadata must use HTTPS.");
        }
        const response = await fetch(api.toString(), {
          headers: { Accept: "application/vnd.github+json" },
          cache: "no-cache",
          signal: controller.signal
        });
        if (response.status === 404) {
          setRelease(null);
          setState("ready");
          return;
        }
        if (!response.ok) throw new Error(`Release lookup failed with HTTP ${response.status}.`);
        const value: unknown = await response.json();
        if (!isPublicRelease(value)) throw new Error("The release response was not in the expected format.");
        setRelease(value);
        setState("ready");
      } catch {
        if (!controller.signal.aborted) {
          setRelease(null);
          setState("error");
        }
      }
    };

    void loadLatestRelease();
    return () => controller.abort();
  }, []);

  return { release, state, releasesPageUrl };
}
