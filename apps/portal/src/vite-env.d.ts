/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_SMARTPRINT_RELEASES_API_URL?: string;
  readonly VITE_SMARTPRINT_RELEASES_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
