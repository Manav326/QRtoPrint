import { createPortal } from "react-dom";
import { ClipboardCheck, Printer, ScanLine } from "lucide-react";
import QRCode from "react-qr-code";
import type { QrStation, ShopProfile } from "./api";

type Props = {
  station: QrStation | null;
  profile: ShopProfile | null;
};

export default function QrStationPrintDocument({ station, profile }: Props) {
  if (!station?.publicUrl) return null;

  const shopName = profile?.displayName?.trim() || station.displayName;

  return createPortal(
    <div className="qr-print-document" aria-hidden="true">
      <article className="qr-print-poster">
        <section className="qr-print-story">
          <header className="qr-print-shop-brand">
            <img src={profile?.logoUrl || "/smartprint-mark.svg"} alt="" />
            <div className="qr-print-shop-copy">
              <strong>{shopName}</strong>
              <small>YOUR LOCAL PRINT SHOP</small>
            </div>
            <span className="qr-print-easy-tag">QUICK &amp; EASY</span>
          </header>

          <div className="qr-print-heading">
            <span className="qr-print-eyebrow">YOUR PRINTING, MADE SIMPLE</span>
            <h1>How it works</h1>
            <p>Send your documents in a few taps. Collect them at the counter.</p>
          </div>

          <div className="qr-print-steps" aria-label="Three easy steps">
            <div className="qr-print-step qr-print-step-scan">
              <div className="qr-print-step-visual">
                <span className="qr-print-step-number">01</span>
                <ScanLine size={23} strokeWidth={2} />
              </div>
              <div className="qr-print-step-copy">
                <strong>Scan, save &amp; send</strong>
                <p>Scan a document or choose a file. Save it in the app, then send it to this shop.</p>
              </div>
              <span className="qr-print-step-arrow" aria-hidden="true">↓</span>
            </div>

            <div className="qr-print-step qr-print-step-review">
              <div className="qr-print-step-visual">
                <span className="qr-print-step-number">02</span>
                <ClipboardCheck size={23} strokeWidth={2} />
              </div>
              <div className="qr-print-step-copy">
                <strong>We review your files</strong>
                <p>The shop checks your documents and approves them before printing.</p>
              </div>
              <span className="qr-print-step-arrow" aria-hidden="true">↓</span>
            </div>

            <div className="qr-print-step qr-print-step-collect">
              <div className="qr-print-step-visual">
                <span className="qr-print-step-number">03</span>
                <Printer size={23} strokeWidth={2} />
              </div>
              <div className="qr-print-step-copy">
                <strong>Print &amp; pick up</strong>
                <p>Your copies are printed and ready for collection at the counter.</p>
              </div>
              <span className="qr-print-step-arrow" aria-hidden="true">✓</span>
            </div>
          </div>

          <footer className="qr-print-powered">
            <img src="/branding/quantaedge-logo.svg" alt="QuantaEdge — Smarter Decisions. Greater Growth." />
            <span>POWERING A SIMPLER PRINT EXPERIENCE</span>
          </footer>
        </section>

        <aside className="qr-print-scan-panel">
          <div className="qr-print-scan-kicker">
            <span className="qr-print-scan-pulse" />
            START HERE
          </div>
          <div className="qr-print-code-frame">
            <QRCode value={station.publicUrl} size={320} level="H" fgColor="#101c35" bgColor="#ffffff" />
          </div>
          <h2>SCAN TO PRINT</h2>
          <p>Open your phone camera and scan this code</p>
          <div className="qr-print-station-name">
            <span className="qr-print-station-dot" />
            {station.displayName}
          </div>
          <div className="qr-print-scan-foot">UPLOAD • REVIEW • COLLECT</div>
        </aside>
      </article>
    </div>,
    document.body
  );
}
