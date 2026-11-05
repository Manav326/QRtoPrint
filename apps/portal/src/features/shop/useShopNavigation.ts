import { useEffect, useRef, useState } from "react";
import type { Dispatch, SetStateAction } from "react";
import { shopPathForSection, shopSectionFromPath } from "../../portal-routing";
import { readSidebarCollapsedPreference, saveSidebarCollapsedPreference } from "../../ui-preferences";
import type { ShopModal, ShopSection } from "./shop-types";

type ShopNavigationOptions = {
  setActive:Dispatch<SetStateAction<ShopSection>>;
  setError:(message:string)=>void;
};

export function useShopNavigation({setActive,setError}:ShopNavigationOptions) {
  useEffect(() => {
    const syncSectionFromPath = () => setActive(shopSectionFromPath(window.location.pathname));
    window.addEventListener("popstate", syncSectionFromPath);
    return () => window.removeEventListener("popstate", syncSectionFromPath);
  }, []);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(readSidebarCollapsedPreference);
  const [accountMenuOpen, setAccountMenuOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [ownerAccountOpen, setOwnerAccountOpen] = useState(false);
  const accountMenuRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!accountMenuOpen) return;
    const closeOnOutsidePointer = (event: PointerEvent) => {
      if (!accountMenuRef.current?.contains(event.target as Node)) setAccountMenuOpen(false);
    };
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") setAccountMenuOpen(false);
    };
    document.addEventListener("pointerdown", closeOnOutsidePointer);
    window.addEventListener("keydown", closeOnEscape);
    return () => {
      document.removeEventListener("pointerdown", closeOnOutsidePointer);
      window.removeEventListener("keydown", closeOnEscape);
    };
  }, [accountMenuOpen]);
  const [modal, setModal] = useState<ShopModal>(null);

  function toggleSidebarCollapsed() {
    setSidebarCollapsed(current => {
      const next = !current;
      saveSidebarCollapsedPreference(next);
      return next;
    });
  }
  const navAction = (section: ShopSection) => {
    const nextPath = shopPathForSection(section);
    if (window.location.pathname !== nextPath) {
      window.history.pushState({}, "", nextPath);
    }
    setActive(section);
    setSidebarOpen(false);
    setAccountMenuOpen(false);
    setError("");
  };

  return {
    sidebarOpen,setSidebarOpen,sidebarCollapsed,setSidebarCollapsed,accountMenuOpen,setAccountMenuOpen,
    helpOpen,setHelpOpen,ownerAccountOpen,setOwnerAccountOpen,accountMenuRef,modal,setModal,navAction,toggleSidebarCollapsed
  };
}
