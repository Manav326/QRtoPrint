import { useEffect, useState } from "react";
import {
  getPlatformOverview, getPlatformTenants, getPlatformTenantStatusHistory,
  updatePlatformTenantStatus, logout,
  type AuthTokens, type PlatformOverview, type PlatformTenantsPage, type PlatformTenant
} from "../../api";

export type PlatformTenantStatusHistoryItem =
  Awaited<ReturnType<typeof getPlatformTenantStatusHistory>>[number];

type PlatformAdminDataOptions = {
  tokens: AuthTokens;
  onSignOut: () => Promise<void>;
};

export function usePlatformAdminData({tokens,onSignOut}:PlatformAdminDataOptions) {
  const [overview,setOverview]=useState<PlatformOverview|null>(null);
  const [overviewLoading,setOverviewLoading]=useState(true);
  const [overviewError,setOverviewError]=useState("");
  const [tenants,setTenants]=useState<PlatformTenantsPage|null>(null);
  const [tenantLoading,setTenantLoading]=useState(false);
  const [tenantError,setTenantError]=useState("");
  const [tenantActionError,setTenantActionError]=useState("");
  const [tenantActionId,setTenantActionId]=useState("");
  const [statusHistoryTenant,setStatusHistoryTenant]=useState<PlatformTenant|null>(null);
  const [statusHistory,setStatusHistory]=useState<PlatformTenantStatusHistoryItem[]|null>(null);
  const [search,setSearch]=useState("");
  const [status,setStatus]=useState("");
  const [page,setPage]=useState(1);
  const [refreshKey,setRefreshKey]=useState(0);
  const [signingOut,setSigningOut]=useState(false);

  useEffect(()=>{
    let active=true;
    setOverviewLoading(true);
    setOverviewError("");
    void getPlatformOverview(tokens.accessToken)
      .then(value=>{if(active)setOverview(value);})
      .catch(exception=>{
        if(active)setOverviewError(exception instanceof Error?exception.message:"Could not load platform overview.");
      })
      .finally(()=>{if(active)setOverviewLoading(false);});
    return ()=>{active=false;};
  },[tokens.accessToken,refreshKey]);

  useEffect(()=>{
    let active=true;
    setTenantLoading(true);
    setTenantError("");
    void getPlatformTenants(tokens.accessToken,{search,status,page,pageSize:20})
      .then(value=>{if(active)setTenants(value);})
      .catch(exception=>{
        if(active)setTenantError(exception instanceof Error?exception.message:"Could not load the tenant directory.");
      })
      .finally(()=>{if(active)setTenantLoading(false);});
    return ()=>{active=false;};
  },[tokens.accessToken,search,status,page,refreshKey]);

  async function changeTenantStatus(tenant:PlatformTenant,nextStatus:"Active"|"Suspended"|"Inactive") {
    const action=nextStatus==="Active"?"reactivate":nextStatus==="Suspended"?"suspend":"permanently deactivate";
    const reason=window.prompt(`Why are you choosing to ${action} ${tenant.businessName}? This reason is recorded in the platform audit history.`);
    if(!reason?.trim()||reason.trim().length<5){
      setTenantActionError("A reason of at least five characters is required. No tenant status was changed.");
      return;
    }
    if(!window.confirm(`Confirm: ${action} ${tenant.businessName}? This affects new print activity for this shop.`))return;
    setTenantActionId(tenant.id);
    setTenantActionError("");
    try{
      await updatePlatformTenantStatus(tokens.accessToken,tenant.id,nextStatus,reason.trim());
      setRefreshKey(value=>value+1);
    }catch(exception){
      setTenantActionError(exception instanceof Error?exception.message:"Could not update this shop's status.");
    }finally{
      setTenantActionId("");
    }
  }

  async function showTenantStatusHistory(tenant:PlatformTenant) {
    setStatusHistoryTenant(tenant);
    setStatusHistory(null);
    try{
      const history=await getPlatformTenantStatusHistory(tokens.accessToken,tenant.id);
      setStatusHistory(history);
    }catch(exception){
      setTenantActionError(exception instanceof Error?exception.message:"Could not load tenant status history.");
    }
  }

  async function signOut() {
    setSigningOut(true);
    try{await logout(tokens.accessToken);}catch{/* Local sign-out still clears the secure session. */}
    try{
      await onSignOut();
    }catch(exception){
      setTenantActionError(exception instanceof Error?exception.message:"Could not clear the secure desktop session.");
      setSigningOut(false);
    }
  }

  return {
    overview,overviewLoading,overviewError,
    tenants,tenantLoading,tenantError,tenantActionError,tenantActionId,
    statusHistoryTenant,statusHistory,search,setSearch,status,setStatus,page,setPage,
    signingOut,refreshData:()=>setRefreshKey(value=>value+1),
    changeTenantStatus,showTenantStatusHistory,
    closeTenantStatusHistory:()=>{setStatusHistoryTenant(null);setStatusHistory(null);}
  };
}
