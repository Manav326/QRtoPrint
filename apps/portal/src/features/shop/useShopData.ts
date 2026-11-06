import { useEffect, useRef, useState } from "react";
import {
  ApiError, getShopAgentDevices, getShopPayments, getShopPrintJobsPage, getShopPrinters,
  loadShopData, storeTokens,
  type AgentDevice, type PrinterDevice, type QrStation, type ShopPayment, type ShopPaymentSettings,
  type ShopPricingProfile, type ShopPrintJob, type ShopProfile, type ShopRefund, type ShopSubscriptionSummary
} from "../../api";
import { isDesktopApp } from "../../desktop-bridge";
import type { AuthTokens } from "../../api";

type ShopDataOptions = {
  accessToken: AuthTokens["accessToken"];
  onSignOut: () => Promise<void>;
  active: string;
  onDesktopAgentSync: () => void;
};

export function useShopData({accessToken,onSignOut,active,onDesktopAgentSync}:ShopDataOptions) {
  const [cloudStatus, setCloudStatus] = useState<"checking" | "connected" | "unavailable">("checking");
  const [profile, setProfile] = useState<ShopProfile | null>(null);
  const [printers, setPrinters] = useState<PrinterDevice[]>([]);
  const [stations, setStations] = useState<QrStation[]>([]);
  const [pricing, setPricing] = useState<ShopPricingProfile | null>(null);
  const [printJobs, setPrintJobs] = useState<ShopPrintJob[]>([]);
  const [journeyJobs, setJourneyJobs] = useState<ShopPrintJob[]>([]);
  const [journeyPayments, setJourneyPayments] = useState<ShopPayment[]>([]);
  const [journeyRefreshing, setJourneyRefreshing] = useState(false);
  const [hasMoreQueueJobs, setHasMoreQueueJobs] = useState(false);
  const [loadingOlderQueueJobs, setLoadingOlderQueueJobs] = useState(false);
  const [queueApiOffset, setQueueApiOffset] = useState(0);
  const [queueSearch, setQueueSearch] = useState("");
  const [agentDevices, setAgentDevices] = useState<AgentDevice[]>([]);
  const [paymentSettings, setPaymentSettings] = useState<ShopPaymentSettings | null>(null);
  const [payments, setPayments] = useState<ShopPayment[]>([]);
  const [refunds, setRefunds] = useState<ShopRefund[]>([]);
  const [subscription, setSubscription] = useState<ShopSubscriptionSummary | null>(null);
  const [apiConnectionState, setApiConnectionState] = useState<"checking" | "connected" | "degraded" | "offline">("checking");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const reloadInFlight = useRef(false);
  const journeyRefreshInFlight = useRef(false);
  const loadingOlderQueueJobsRef = useRef(false);
  const queueSearchGeneration = useRef(0);
  const queuePageGeneration = useRef(0);
  const queueFirstPageIds = useRef<Set<string>>(new Set());
  const agentDevicesRef = useRef<AgentDevice[]>([]);
  agentDevicesRef.current = agentDevices;

  async function reload() {
    if (reloadInFlight.current) return;
    queueSearchGeneration.current++;
    queuePageGeneration.current++;
    reloadInFlight.current = true;
    setApiConnectionState("checking");
    setLoading(true);
    setError("");
    try {
      const [snapshot, unfilteredJourneyJobs] = await Promise.all([
        loadShopData(accessToken, queueSearch),
        queueSearch.trim()
          ? getShopPrintJobsPage(accessToken, 0, 100)
          : Promise.resolve(null)
      ]);
      setProfile(snapshot.profile);
      setPrinters(snapshot.printers);
      setStations(snapshot.stations);
      setPricing(snapshot.pricing);
      setPrintJobs(snapshot.printJobs);
      setJourneyJobs(unfilteredJourneyJobs ?? snapshot.printJobs);
      setJourneyPayments(snapshot.payments);
      queueFirstPageIds.current = new Set(snapshot.printJobs.map(job => job.id));
      setHasMoreQueueJobs(snapshot.printJobs.length === 100);
      setQueueApiOffset(snapshot.printJobs.length);
      agentDevicesRef.current = snapshot.agentDevices;
      setAgentDevices(snapshot.agentDevices);
      setPaymentSettings(snapshot.paymentSettings);
      setPayments(snapshot.payments);
      setRefunds(snapshot.refunds);
      setSubscription(snapshot.subscription);
      setApiConnectionState("connected");
      setCloudStatus("connected");
      if (isDesktopApp()) {
        // The Windows service may still be starting after installation or reboot.
        // Keep discovery off the initial shop-data load and retry quietly.
        onDesktopAgentSync();
      }
    } catch (exception) {
      setCloudStatus("unavailable");
      if (exception instanceof ApiError && exception.status === 401) {
        storeTokens(null);
        try { await onSignOut(); }
        catch (signOutError) { setError(signOutError instanceof Error ? signOutError.message : "Could not clear the secure desktop session."); }
      } else {
        handleShopApiError(exception, "Could not load your shop.");
      }
    } finally {
      setLoading(false);
      reloadInFlight.current = false;
    }
  }

  function handleShopApiError(exception: unknown, fallback: string) {
    if (exception instanceof ApiError && exception.status === 401) {
      storeTokens(null);
      onSignOut();
      return;
    }
    const status = exception instanceof ApiError ? exception.status : 0;
    setApiConnectionState(status === 0 ? "offline" : status >= 500 ? "degraded" : "connected");
    setError(exception instanceof Error ? exception.message : fallback);
  }

  async function refreshPrintQueue() {
    const generation = queuePageGeneration.current;
    try {
      const [latest, latestPrinters, latestAgents] = await Promise.all([
        getShopPrintJobsPage(accessToken, 0, 100, queueSearch),
        getShopPrinters(accessToken),
        getShopAgentDevices(accessToken)
      ]);
      if (generation !== queuePageGeneration.current) return;
      setApiConnectionState("connected");
      setPrinters(latestPrinters);
      setAgentDevices(latestAgents);
      const latestIds = new Set(latest.map(job => job.id));
      const firstPageMembershipChanged = latest.length !== queueFirstPageIds.current.size ||
        latest.some(job => !queueFirstPageIds.current.has(job.id));
      if (firstPageMembershipChanged) {
        // Any insert/removal shifts every offset page. Reset pagination to the
        // current first page so no older records are skipped or duplicated.
        queuePageGeneration.current++;
        queueFirstPageIds.current = latestIds;
        setPrintJobs(latest);
        setQueueApiOffset(latest.length);
        setHasMoreQueueJobs(latest.length === 100);
        return;
      }
      setPrintJobs(current => {
        const byId = new Map(current.map(job => [job.id, job]));
        for (const job of latest) byId.set(job.id, job);
        return Array.from(byId.values()).sort((left, right) =>
          Date.parse(right.createdUtc) - Date.parse(left.createdUtc) || right.id.localeCompare(left.id)
        );
      });
      setHasMoreQueueJobs(latest.length === 100 || printJobs.length > latest.length);
    } catch (exception) {
      if (generation !== queuePageGeneration.current) return;
      handleShopApiError(exception, "Could not refresh the print queue.");
    }
  }

  async function loadOlderQueueJobs() {
    if (loadingOlderQueueJobsRef.current || !hasMoreQueueJobs) return;
    loadingOlderQueueJobsRef.current = true;
    const generation = queuePageGeneration.current;
    const offset = queueApiOffset;
    setLoadingOlderQueueJobs(true);
    try {
      const older = await getShopPrintJobsPage(accessToken, offset, 100, queueSearch);
      if (generation !== queuePageGeneration.current) return;
      setPrintJobs(current => {
        const knownIds = new Set(current.map(job => job.id));
        const merged = [...current, ...older.filter(job => !knownIds.has(job.id))];
        return merged.sort((left, right) =>
          Date.parse(right.createdUtc) - Date.parse(left.createdUtc) || right.id.localeCompare(left.id)
        );
      });
      setQueueApiOffset(offset => offset + older.length);
      setHasMoreQueueJobs(older.length === 100);
    } catch (exception) {
      if (generation !== queuePageGeneration.current) return;
      handleShopApiError(exception, "Could not load older print jobs.");
    } finally {
      loadingOlderQueueJobsRef.current = false;
      setLoadingOlderQueueJobs(false);
    }
  }

  async function searchPrintQueue(value: string) {
    const normalized = value.trim().slice(0, 120);
    const generation = ++queueSearchGeneration.current;
    queuePageGeneration.current++;
    setQueueSearch(normalized);
    try {
      const matches = await getShopPrintJobsPage(accessToken, 0, 100, normalized);
      if (generation !== queueSearchGeneration.current) return;
      queueFirstPageIds.current = new Set(matches.map(job => job.id));
      setPrintJobs(matches);
      setQueueApiOffset(matches.length);
      setHasMoreQueueJobs(matches.length === 100);
    } catch (exception) {
      if (generation !== queueSearchGeneration.current) return;
      handleShopApiError(exception, "Could not search the print queue.");
    }
  }

  async function refreshOrderJourney() {
    if (journeyRefreshInFlight.current) return;
    journeyRefreshInFlight.current = true;
    setJourneyRefreshing(true);
    try {
      const [latestJobs, latestPayments] = await Promise.all([
        getShopPrintJobsPage(accessToken, 0, 100),
        getShopPayments(accessToken)
      ]);
      setJourneyJobs(latestJobs);
      setJourneyPayments(latestPayments);
      setApiConnectionState("connected");
    } catch (exception) {
      handleShopApiError(exception, "Could not refresh the order journey.");
    } finally {
      journeyRefreshInFlight.current = false;
      setJourneyRefreshing(false);
    }
  }



  useEffect(() => {
    void reload();
    // A token change is the only trigger; mutations call reload explicitly.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [accessToken]);

  useEffect(() => {
    if (active === "Print Queue") return;
    let mounted = true;
    let inFlight = false;
    const refreshDevices = async () => {
      if (inFlight) return;
      inFlight = true;
      try {
        const [latestPrinters, latestAgents] = await Promise.all([
          getShopPrinters(accessToken),
          getShopAgentDevices(accessToken)
        ]);
        if (mounted) {
          setPrinters(latestPrinters);
          setAgentDevices(latestAgents);
          setApiConnectionState("connected");
        }
      } catch (exception) {
        if (mounted) {
          if (exception instanceof ApiError && exception.status === 401) {
            storeTokens(null);
            onSignOut();
          } else {
            const status = exception instanceof ApiError ? exception.status : 0;
            setApiConnectionState(status === 0 ? "offline" : status >= 500 ? "degraded" : "connected");
          }
        }
      } finally {
        inFlight = false;
      }
    };
    void refreshDevices();
    const timer = window.setInterval(() => void refreshDevices(), 15_000);
    return () => {
      mounted = false;
      window.clearInterval(timer);
    };
  }, [active, accessToken]);

  return {
    cloudStatus, setCloudStatus, profile, setProfile, printers, setPrinters, stations, setStations,
    pricing, setPricing, printJobs, setPrintJobs, journeyJobs, setJourneyJobs, journeyPayments,
    setJourneyPayments, journeyRefreshing, setJourneyRefreshing, hasMoreQueueJobs, setHasMoreQueueJobs,
    loadingOlderQueueJobs, setLoadingOlderQueueJobs, queueApiOffset, setQueueApiOffset, queueSearch,
    setQueueSearch, agentDevices, setAgentDevices, agentDevicesRef, paymentSettings, setPaymentSettings,
    payments, setPayments, refunds, setRefunds, subscription, setSubscription, apiConnectionState,
    setApiConnectionState, loading, setLoading, error, setError, reload, handleShopApiError,
    refreshPrintQueue, loadOlderQueueJobs, searchPrintQueue, refreshOrderJourney
  };
}
