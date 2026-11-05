import { useEffect, useMemo, useState } from "react";
import {
  createPlatformPlan, getPlatformPlans, publishPlatformPlanVersion, savePlatformPlanDraft, updatePlatformPlan,
  type AuthTokens, type PlatformPlan, type PlatformPlanVersion
} from "./api";
import {
  emptyForm, formFromVersion, draftVersion, latestVersion, toVersionInput, optionalPreview,
  type PlanForm
} from "./platform-plans-model";

export function usePlatformPlans(tokens:AuthTokens) {
  const [plans, setPlans] = useState<PlatformPlan[]>([]);
  const [selectedPlanId, setSelectedPlanId] = useState("");
  const [creating, setCreating] = useState(false);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [active, setActive] = useState(true);
  const [form, setForm] = useState<PlanForm>({ ...emptyForm });
  
  const selectedPlan = useMemo(
    () => plans.find(plan => plan.id === selectedPlanId) ?? null,
    [plans, selectedPlanId]
  );
  const currentDraft = selectedPlan ? draftVersion(selectedPlan) : undefined;
  const hasDraft = currentDraft?.status === "Draft";
  const isSystemPlan = selectedPlan?.isSystemPlan ?? false;
  
  async function reload(selectId?: string) {
    setLoading(true);
    setError("");
    try {
      const result = await getPlatformPlans(tokens.accessToken);
      setPlans(result);
      const nextId = selectId ?? selectedPlanId;
      if (nextId && result.some(plan => plan.id === nextId)) {
        setSelectedPlanId(nextId);
      } else if (result.length) {
        setSelectedPlanId((result.find(plan => !plan.isSystemPlan) ?? result[0]).id);
      } else {
        setSelectedPlanId("");
      }
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not load subscription plans.");
    } finally {
      setLoading(false);
    }
  }
  
  useEffect(() => {
    void reload();
    // The token is stable for the duration of this platform session.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tokens.accessToken]);
  
  useEffect(() => {
    if (creating) return;
    if (!selectedPlan) {
      setName("");
      setDescription("");
      setCode("");
      setActive(true);
      setForm({ ...emptyForm });
      return;
    }
    setCode(selectedPlan.code);
    setName(selectedPlan.name);
    setDescription(selectedPlan.description ?? "");
    setActive(selectedPlan.isActive);
    setForm(formFromVersion(draftVersion(selectedPlan)));
  }, [selectedPlanId, plans, creating]);
  
  function startCreate() {
    setCreating(true);
    setSelectedPlanId("");
    setError("");
    setSuccess("");
    setCode("");
    setName("");
    setDescription("");
    setActive(true);
    setForm({ ...emptyForm });
  }
  
  function changeForm<K extends keyof PlanForm>(key: K, value: PlanForm[K]) {
    setForm(previous => ({ ...previous, [key]: value }));
  }
  
  async function savePlan() {
    setBusy("save");
    setError("");
    setSuccess("");
    try {
      const version = toVersionInput(form);
      if (creating) {
        if (!/^[A-Za-z][A-Za-z0-9_]{1,39}$/.test(code.trim())) {
          throw new Error("Plan code must contain 2–40 letters, digits or underscores and start with a letter.");
        }
        if (!name.trim()) throw new Error("Plan name is required.");
        const result = await createPlatformPlan(tokens.accessToken, {
          code: code.trim().toUpperCase(),
          name: name.trim(),
          description: description.trim() || undefined,
          version
        });
        setCreating(false);
        await reload(result.id);
        setSuccess("Plan created. Its first version is still a draft and is not available for subscription assignment.");
      } else if (selectedPlan) {
        await savePlatformPlanDraft(tokens.accessToken, selectedPlan.id, version);
        await reload(selectedPlan.id);
        setSuccess("Draft version saved. Published versions and existing subscriptions remain unchanged.");
      }
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not save the plan.");
    } finally {
      setBusy("");
    }
  }
  
  async function saveMetadata() {
    if (!selectedPlan) return;
    setBusy("metadata");
    setError("");
    setSuccess("");
    try {
      await updatePlatformPlan(tokens.accessToken, selectedPlan.id, {
        name: name.trim(),
        description: description.trim() || undefined,
        isActive: active
      });
      await reload(selectedPlan.id);
      setSuccess("Plan details updated.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not update plan details.");
    } finally {
      setBusy("");
    }
  }
  
  async function publishDraft() {
    if (!selectedPlan || !currentDraft || currentDraft.status !== "Draft") return;
    setBusy("publish");
    setError("");
    setSuccess("");
    try {
      await publishPlatformPlanVersion(tokens.accessToken, selectedPlan.id, currentDraft.version);
      await reload(selectedPlan.id);
      setSuccess(`Version ${currentDraft.version} published. New subscriptions may reference this version when assignment is implemented.`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not publish this plan version.");
    } finally {
      setBusy("");
    }
  }
  
  
  const monthlyMinor=optionalPreview(form.monthlyPriceMinor);
  const annualMinor=optionalPreview(form.annualPriceMinor);
  return {
    plans,selectedPlanId,setSelectedPlanId,creating,setCreating,loading,busy,error,setError,success,setSuccess,
    code,setCode,name,setName,description,setDescription,active,setActive,form,setForm,
    selectedPlan,currentDraft,hasDraft,isSystemPlan,monthlyMinor,annualMinor,
    reload,startCreate,changeForm,savePlan,saveMetadata,publishDraft
  };
}
