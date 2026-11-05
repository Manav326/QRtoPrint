import { ArrowRight, Check, FileText, Monitor, Printer, QrCode, Wallet } from "lucide-react";
import Modal from "../../../shared/ui/Modal";
import type { ShopSection } from "../shop-types";

export function HelpSupportModal({ onClose, onNavigate }: { onClose: () => void; onNavigate: (section: ShopSection) => void }) {
  const topics: { section: ShopSection; title: string; description: string; icon: typeof FileText }[] = [
    { section: "Print Queue", title: "Print jobs and approvals", description: "Review incoming jobs, decisions, print status and refunds.", icon: FileText },
    { section: "Printers", title: "Printers and connections", description: "Check printer setup, availability and device details.", icon: Printer },
    { section: "Print Agent", title: "Shop computer and Print Agent", description: "Pair or reconnect the Windows computer that runs printing.", icon: Monitor },
    { section: "QR Stations", title: "QR stations", description: "Manage counter QR codes and their assigned printers.", icon: QrCode },
    { section: "Pricing", title: "Pricing", description: "Review the rates customers see before they submit a job.", icon: Wallet }
  ];

  return (
    <Modal title="Help & support" subtitle="Choose the part of your shop workspace you need help with." onClose={onClose}>
      <div className="help-topic-list">
        {topics.map(({ section, title, description, icon: Icon }) => (
          <button type="button" className="help-topic" key={section} onClick={() => onNavigate(section)}>
            <span className="help-topic-icon"><Icon size={17}/></span>
            <span className="help-topic-copy"><strong>{title}</strong><small>{description}</small></span>
            <ArrowRight size={16}/>
          </button>
        ))}
      </div>
      <p className="help-modal-note">These shortcuts open the existing shop tools. They do not change your settings or print jobs.</p>
    </Modal>
  );
}

