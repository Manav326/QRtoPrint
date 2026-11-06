import { useState } from "react";

type ActionDialogState = {
  title:string;
  message:string;
  inputLabel?:string;
  confirmLabel:string;
  resolve:(value:string|null)=>void;
};
type ActionDialogOptions = {
  title:string;
  message:string;
  inputLabel?:string;
  confirmLabel?:string;
};

export function useShopDialogs() {
  const [actionDialog,setActionDialog]=useState<ActionDialogState|null>(null);
  const [actionDialogValue,setActionDialogValue]=useState("");

  function requestActionDialog(options:ActionDialogOptions):Promise<string|null> {
    return new Promise(resolve=>{
      setActionDialogValue("");
      setActionDialog({...options,confirmLabel:options.confirmLabel||"Confirm",resolve});
    });
  }

  async function confirmInApp(message:string,title="Please confirm",confirmLabel="Confirm"):Promise<boolean> {
    return (await requestActionDialog({title,message,confirmLabel}))!==null;
  }

  async function promptInApp(message:string,title="Enter details",inputLabel="Reason"):Promise<string|null> {
    return requestActionDialog({title,message,inputLabel,confirmLabel:"Continue"});
  }

  function closeActionDialog() {
    const current=actionDialog;
    setActionDialog(null);
    current?.resolve(null);
  }

  function submitActionDialog() {
    const current=actionDialog;
    setActionDialog(null);
    current?.resolve(actionDialogValue);
  }

  return {actionDialog,actionDialogValue,setActionDialogValue,confirmInApp,promptInApp,closeActionDialog,submitActionDialog};
}
