import { useEffect, useState } from "react";
import { ArrowRight, Printer, ShieldCheck } from "lucide-react";
import AuthScreen from "./AuthScreen";
import PublicLandingPage from "./PublicLandingPage";
import PlatformAdminConsole from "./PlatformAdminConsole";
import PlatformAdminAuthScreen from "./PlatformAdminAuthScreen";
import { resolvePortalRoute } from "./portal-routing";
import { isPlatformAdminSession, readStoredTokens, storeTokens, type AuthTokens } from "./api";
import { applyThemePreference, readDarkModePreference, saveDarkModePreference } from "./ui-preferences";
import CustomerEntry from "./features/customer/CustomerEntry";
import ThemeToggleButton from "./ThemeToggleButton";
import ShopConsole from "./features/shop/ShopConsole";

export default function App() {
  const route = resolvePortalRoute(window.location.pathname);
  const [dark, setDark] = useState(readDarkModePreference);
  const toggleTheme = () => setDark(value => !value);
  useEffect(() => {
    saveDarkModePreference(dark);
    applyThemePreference(dark);
  }, [dark]);
  useEffect(() => {
    const syncNativeTheme = (event: Event) => {
      const selected = (event as CustomEvent<string>).detail;
      if (selected === "light" || selected === "dark") setDark(selected === "dark");
    };
    window.addEventListener("smartprint-theme-change", syncNativeTheme);
    return () => window.removeEventListener("smartprint-theme-change", syncNativeTheme);
  }, []);
  const [tokens, setTokens] = useState<AuthTokens | null>(() =>
    route.kind === "admin" ? readStoredTokens("admin")
      : route.kind === "shop" ? readStoredTokens("shop")
      : null
  );

  if (route.kind === "station") {
    return <CustomerEntry publicCode={route.publicCode} dark={dark} onToggleTheme={toggleTheme} />;
  }

  if (route.kind === "public") {
    return <PublicLandingPage dark={dark} onToggleTheme={toggleTheme} />;
  }

  if (route.kind === "admin") {
    const onAdminSignOut = async () => {
      await storeTokens(null, "admin");
      setTokens(null);
    };
    if (!tokens) return <PlatformAdminAuthScreen onAuthenticated={setTokens} dark={dark} onToggleTheme={toggleTheme}/>;
    if (!isPlatformAdminSession(tokens)) {
      return <ScopeAccessDenied scope="platform administration" onSignOut={onAdminSignOut} dark={dark} onToggleTheme={toggleTheme}/>;
    }
    return <PlatformAdminConsole tokens={tokens} onSignOut={onAdminSignOut} dark={dark} onToggleTheme={toggleTheme}/>;
  }

  const onShopSignOut = async () => {
    await storeTokens(null, "shop");
    setTokens(null);
  };
  if (!tokens) return <AuthScreen onAuthenticated={setTokens} dark={dark} onToggleTheme={toggleTheme}/>;
  if (isPlatformAdminSession(tokens)) {
    return <ScopeAccessDenied scope="the shop workspace" onSignOut={onShopSignOut} dark={dark} onToggleTheme={toggleTheme}/>;
  }
  return <ShopConsole tokens={tokens} onSignOut={onShopSignOut} dark={dark} onToggleTheme={toggleTheme}/>;
}

function ScopeAccessDenied({ scope, onSignOut, dark, onToggleTheme }: { scope: string; onSignOut: () => Promise<void>; dark: boolean; onToggleTheme: () => void }) {
  const [busy, setBusy] = useState(false);
  async function signOut() {
    setBusy(true);
    try { await onSignOut(); } finally { setBusy(false); }
  }
  return (
    <main className="auth-screen">
      <header className="auth-topbar">
        <div className="brand-lockup auth-brand"><div className="brand-mark"><Printer size={21}/></div><div><div className="brand-name">smart<span>print</span></div><div className="brand-caption">BY QRtoPrint</div></div></div>
        <div className="auth-top-actions"><ThemeToggleButton dark={dark} onToggle={onToggleTheme}/><a className="auth-home-link" href="/">Back to website</a></div>
      </header>
      <section className="auth-card" style={{ maxWidth: 520, margin: "12vh auto" }}>
        <div className="auth-card-heading"><div className="auth-card-icon"><ShieldCheck size={20}/></div><div><span>APPLICATION SCOPE MISMATCH</span><h2>Access not available</h2><p>This signed-in account is not authorized for {scope}.</p></div></div>
        <button className="auth-submit" onClick={() => void signOut()} disabled={busy}>{busy ? <span className="button-spinner"/> : "Sign out and use another account"} <ArrowRight size={16}/></button>
      </section>
    </main>
  );
}
