import { Moon, Sun } from "lucide-react";

export default function ThemeToggleButton({
  dark,
  onToggle,
  className = ""
}: {
  dark: boolean;
  onToggle: () => void;
  className?: string;
}) {
  return (
    <button
      type="button"
      className={`theme-toggle-button ${className}`.trim()}
      onClick={onToggle}
      aria-label={dark ? "Switch to light theme" : "Switch to dark theme"}
      title={dark ? "Switch to light theme" : "Switch to dark theme"}
      aria-pressed={dark}
    >
      {dark ? <Sun size={17}/> : <Moon size={17}/>}
      <span>{dark ? "Light" : "Dark"} mode</span>
    </button>
  );
}
