import { useState } from "react";
import { ShieldCheck } from "lucide-react";
import type { ShopProfile } from "../../../api";
import Modal from "../../../shared/ui/Modal";

export function ShopOwnerAccountModal({ profile, busy, onClose, onSave }: {
  profile: ShopProfile;
  busy: boolean;
  onClose: () => void;
  onSave: (values: { ownerName: string; email: string; currentPassword?: string }) => Promise<void>;
}) {
  const [ownerName, setOwnerName] = useState(profile.ownerName);
  const [email, setEmail] = useState(profile.ownerEmail);
  const [currentPassword, setCurrentPassword] = useState("");
  const [saveError, setSaveError] = useState("");
  const emailChanged = email.trim().toLowerCase() !== profile.ownerEmail.trim().toLowerCase();
  return (
    <Modal title="Personal details" subtitle="Update the name and sign-in email for your shop owner account." onClose={onClose}>
      <form className="modal-form account-edit-form" onSubmit={async event => {
        event.preventDefault();
        setSaveError("");
        try {
          await onSave({
            ownerName: ownerName.trim(),
            email: email.trim(),
            currentPassword: emailChanged ? currentPassword : undefined
          });
          onClose();
        } catch (exception) {
          setSaveError(exception instanceof Error ? exception.message : "Could not update your personal details.");
        }
      }}>
        <label>Your name
          <input value={ownerName} onChange={event => setOwnerName(event.target.value)}
            autoComplete="name" maxLength={160} required/>
        </label>
        <label>Sign-in email
          <input type="email" value={email}
            onChange={event => setEmail(event.currentTarget.value)}
            onInput={event => setEmail(event.currentTarget.value)}
            autoComplete="email" maxLength={256} required/>
        </label>
        {emailChanged && <>
          <div className="modal-note account-email-change-note">
            <ShieldCheck size={16}/>
            <span>This address becomes your sign-in email immediately. Make sure it is correct. Your current password is required to confirm the change.</span>
          </div>
          <label>Current password
            <input type="password" value={currentPassword}
              onChange={event => setCurrentPassword(event.target.value)}
              autoComplete="current-password" required/>
          </label>
        </>}
        {saveError && <div className="inline-error account-edit-error" role="alert">{saveError}</div>}
        <div className="modal-actions">
          <button type="button" className="secondary-button" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="primary-button" disabled={busy || !ownerName.trim() || !email.trim() || (emailChanged && !currentPassword)}>
            {busy ? <span className="button-spinner"/> : "Save personal details"}
          </button>
        </div>
      </form>
    </Modal>
  );
}

