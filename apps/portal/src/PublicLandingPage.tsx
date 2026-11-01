import {
  ArrowRight, ArrowUpRight, Check, ChevronRight, Cloud, Download, FileCheck2, FileText,
  LockKeyhole, Monitor, Printer, QrCode, ShieldCheck, Smartphone, Store, Wifi
} from "lucide-react";
import "./public-landing.css";

export default function PublicLandingPage() {
  return (
    <div className="sp-public">
      <header className="sp-public-header">
        <a className="sp-public-brand" href="/" aria-label="SmartPrint home">
          <span className="sp-public-brand-mark"><Printer size={21} strokeWidth={2.2}/></span>
          <span className="sp-public-brand-copy">
            <strong>smart<span>print</span></strong>
            <small>BY QUANTAEDGE</small>
          </span>
        </a>
        <nav className="sp-public-nav" aria-label="Main navigation">
          <a href="#how-it-works">How it works</a>
          <a href="#for-shops">For shops</a>
          <a href="#for-customers">For customers</a>
          <a href="#downloads">Downloads</a>
        </nav>
        <a className="sp-shop-entry" href="/shop">
          <span className="sp-shop-entry-icon"><Store size={18}/><Printer size={10}/></span>
          <span>For print shops</span>
          <ArrowUpRight size={15}/>
        </a>
      </header>

      <main>
        <section className="sp-hero">
          <div className="sp-hero-copy">
            <div className="sp-eyebrow"><span className="sp-eyebrow-dot"/> PRINTING, CONNECTED</div>
            <h1>From their phone<br/>to <span>your printer.</span><br/>Without the back-and-forth.</h1>
            <p className="sp-hero-lede">SmartPrint brings local print shops and their customers together. A quick QR scan starts a clear, secure journey from document to print.</p>
            <div className="sp-hero-actions">
              <a className="sp-button-primary" href="/shop?mode=register">Bring your shop online <ArrowRight size={17}/></a>
              <a className="sp-button-quiet" href="#how-it-works">See how it works <ChevronRight size={17}/></a>
            </div>
            <div className="sp-hero-trust"><span><ShieldCheck size={15}/> Shop-controlled print approval</span><span><LockKeyhole size={14}/> Secure job handoff</span></div>
          </div>

          <div className="sp-hero-art" aria-label="Illustration of a customer phone connecting to a print shop">
            <div className="sp-art-orbit sp-art-orbit-one"/>
            <div className="sp-art-orbit sp-art-orbit-two"/>
            <div className="sp-art-customer">
              <div className="sp-art-phone-top"><span/><i/></div>
              <div className="sp-art-phone-screen">
                <div className="sp-art-phone-brand"><span><Printer size={11}/></span> smartprint</div>
                <div className="sp-art-document"><FileText size={23}/><div><b>Project-notes.pdf</b><small>12 pages · PDF</small></div><Check size={14}/></div>
                <div className="sp-art-upload"><span/><span/><span/></div>
                <div className="sp-art-phone-button">Ready to print <ArrowRight size={11}/></div>
              </div>
              <div className="sp-art-phone-caption"><Smartphone size={12}/> CUSTOMER</div>
            </div>
            <div className="sp-art-connection"><span/><span/><span/><small>SECURE CONNECTION</small></div>
            <div className="sp-art-shop">
              <div className="sp-art-shop-head"><span className="sp-art-shop-icon"><Store size={16}/></span><div><b>Your print shop</b><small>Workspace connected</small></div><i/></div>
              <div className="sp-art-printer"><span><Printer size={25}/></span><div><b>Shop printer</b><small>Ready for the next job</small></div><em><Check size={12}/></em></div>
              <div className="sp-art-shop-foot"><span><Wifi size={12}/> Agent online</span><span>Job received</span></div>
              <div className="sp-art-shop-caption">YOUR SHOP</div>
            </div>
            <div className="sp-art-floating sp-art-floating-one"><QrCode size={16}/><span>Scan to start</span></div>
            <div className="sp-art-floating sp-art-floating-two"><Check size={15}/><span>Shop approved</span></div>
          </div>
        </section>

        <section className="sp-proof-strip" aria-label="SmartPrint workflow highlights">
          <div><QrCode/><span><b>One QR code</b><small>Connect each counter</small></span></div>
          <div><FileCheck2/><span><b>Clear job review</b><small>See each print request</small></span></div>
          <div><ShieldCheck/><span><b>Shop stays in control</b><small>Approve before printing</small></span></div>
          <div><Printer/><span><b>Your printers</b><small>Use the shop's setup</small></span></div>
        </section>

        <section className="sp-section sp-how" id="how-it-works">
          <div className="sp-section-heading">
            <div className="sp-eyebrow">A SIMPLE, CONNECTED FLOW</div>
            <h2>Three steps. One smoother print experience.</h2>
            <p>No messaging files back and forth. No guessing what the customer needs printed.</p>
          </div>
          <div className="sp-steps">
            <article className="sp-step-card">
              <span className="sp-step-number">01</span><div className="sp-step-icon"><QrCode size={24}/></div>
              <h3>Scan &amp; send</h3><p>Customers scan your shop's QR code, add their documents and choose their print settings.</p>
              <span className="sp-step-tag">CUSTOMER</span>
            </article>
            <article className="sp-step-card">
              <span className="sp-step-number">02</span><div className="sp-step-icon"><FileCheck2 size={24}/></div>
              <h3>Review &amp; approve</h3><p>Your team reviews the document, checks the quote and payment, and authorises the job.</p>
              <span className="sp-step-tag">YOUR SHOP</span>
            </article>
            <article className="sp-step-card">
              <span className="sp-step-number">03</span><div className="sp-step-icon"><Printer size={24}/></div>
              <h3>Print &amp; hand over</h3><p>The connected Windows Agent routes approved jobs to the selected shop printer for output.</p>
              <span className="sp-step-tag">CONNECTED PRINTER</span>
            </article>
          </div>
        </section>

        <section className="sp-two-sided">
          <article className="sp-audience-card sp-customer-card" id="for-customers">
            <div className="sp-audience-kicker"><Smartphone size={15}/> FOR CUSTOMERS</div>
            <h2>Printing starts<br/>where you are.</h2>
            <p>Use your phone to connect to a nearby participating shop. Upload documents, review the print details and follow the job without passing files around.</p>
            <ul><li><Check/> Scan the shop's QR code</li><li><Check/> Upload supported documents</li><li><Check/> Review the quote and track the job</li></ul>
            <div className="sp-audience-visual sp-customer-visual"><div className="sp-customer-mini-phone"><div/><span><FileText size={24}/></span><b>Your document</b><small>Ready to submit</small></div><div className="sp-customer-mini-cloud"><Cloud size={34}/><span>Connected to shop</span></div></div>
            <a href="#downloads" className="sp-text-link">Explore the Android app <ArrowRight size={16}/></a>
          </article>
          <article className="sp-audience-card sp-shop-card" id="for-shops">
            <div className="sp-audience-kicker"><Store size={15}/> FOR PRINT SHOPS</div>
            <h2>Your counter.<br/>Your workflow.</h2>
            <p>Keep incoming print requests, pricing, printers and shop approvals in one workspace built around the way local print shops operate.</p>
            <ul><li><Check/> Manage QR stations and printers</li><li><Check/> Review jobs before authorising them</li><li><Check/> Keep control of shop payments and output</li></ul>
            <div className="sp-audience-visual sp-shop-visual"><div className="sp-shop-mini-window"><div className="sp-window-dots"><i/><i/><i/></div><div className="sp-mini-stat-row"><span/><span/><span/></div><div className="sp-mini-job"><FileText size={15}/><span/><em>IN REVIEW</em></div><div className="sp-mini-job"><Printer size={15}/><span/><em>APPROVED</em></div></div></div>
            <a href="/shop" className="sp-text-link">Open your shop workspace <ArrowRight size={16}/></a>
          </article>
        </section>

        <section className="sp-connection-section">
          <div className="sp-connection-copy">
            <div className="sp-eyebrow">BUILT AROUND YOUR SHOP</div>
            <h2>Your printers stay at your shop. Your workflow gets connected.</h2>
            <p>The SmartPrint Windows Agent connects the shop computer to the workspace. It discovers installed printer queues and securely receives only jobs authorised by the shop.</p>
            <div className="sp-connection-points"><span><Check/> Your existing Windows printer setup</span><span><Check/> Shop-controlled job authorisation</span><span><Check/> Local or cloud-assisted document transfer</span></div>
          </div>
          <div className="sp-connection-visual"><div className="sp-device-card"><Monitor size={29}/><b>Shop computer</b><small>SmartPrint Agent</small><span className="sp-online"><i/> Connected</span></div><div className="sp-connection-line"><span/><span/><span/></div><div className="sp-device-printer"><Printer size={35}/><b>Installed printer</b><small>Shop's printer queue</small></div><div className="sp-secure-pill"><LockKeyhole size={12}/> Authorised connection</div></div>
        </section>

        <section className="sp-download-section" id="downloads">
          <div className="sp-section-heading">
            <div className="sp-eyebrow">GET STARTED WITH SMARTPRINT</div>
            <h2>The right app for the right job.</h2>
            <p>Set up the shop computer or bring the customer experience to Android.</p>
          </div>
          <div className="sp-download-grid">
            <article className="sp-download-card">
              <div className="sp-download-icon sp-windows-icon"><Monitor size={25}/></div><div className="sp-download-platform">FOR SHOP OWNERS · WINDOWS 10/11</div>
              <h3>SmartPrint for Windows</h3><p>Install the shop Agent on the computer connected to your printers. The guided setup will help connect it to your shop workspace.</p>
              <span className="sp-download-unavailable sp-download-button" aria-disabled="true">Signed Windows setup coming soon</span>
              <small className="sp-download-note">Windows x64 · Guided installation · Public code signing pending</small>
              <p className="sp-release-pending">The installer has passed automated build validation. It will be downloadable after the trusted Windows publisher certificate is configured and the signed release is published.</p>
            </article>
            <article className="sp-download-card">
              <div className="sp-download-icon sp-android-icon"><Smartphone size={25}/></div><div className="sp-download-platform">FOR CUSTOMERS · ANDROID</div>
              <h3>SmartPrint for Android</h3><p>Scan shop QR codes, organise documents in your library and send files through the connected SmartPrint customer journey.</p>
              <span className="sp-download-unavailable sp-download-button" aria-disabled="true">Signed Android APK coming soon</span>
              <small className="sp-download-note">Android 8.0 or later · APK installation · Production signing pending</small>
              <p className="sp-release-pending">Android unit and emulator tests have passed. The customer APK will be available after the permanent release keystore is configured and the signed release is published.</p>
            </article>
          </div>
          <p className="sp-download-footnote"><ShieldCheck size={14}/> Download links are served from SmartPrint's official release channel.</p>
        </section>

        <section className="sp-final-cta">
          <div><div className="sp-eyebrow">READY TO CONNECT YOUR SHOP?</div><h2>Give every print job<br/>a clearer way forward.</h2><p>Set up your shop workspace and bring your customers into the flow.</p></div>
          <a className="sp-button-light" href="/shop?mode=register">Create your shop account <ArrowRight size={17}/></a>
        </section>
      </main>
      <footer className="sp-public-footer">
        <a className="sp-public-brand" href="/"><span className="sp-public-brand-mark"><Printer size={19}/></span><span className="sp-public-brand-copy"><strong>smart<span>print</span></strong><small>BY QUANTAEDGE</small></span></a>
        <span>© {new Date().getFullYear()} SmartPrint by QuantaEdge</span>
        <div><a href="#how-it-works">How it works</a><a href="#downloads">Downloads</a><a href="/shop">For print shops</a></div>
      </footer>
    </div>
  );
}

function ArrowDownIcon() {
  return <Download size={16}/>;
}
