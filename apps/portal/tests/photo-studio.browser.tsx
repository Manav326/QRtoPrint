import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import PhotoStudio, { type PreparedPhotoSheet } from "../src/PhotoStudio";

const query = new URLSearchParams(window.location.search);
const mode = query.get("mode") === "shop" ? "shop" : "customer";
const dark = query.get("theme") === "dark";
const photoPrices = { "35x45": 2500, "25x35": 1800, "2x2": 3000, "4x6": 6000 };

function createFixturePhoto(): Promise<File> {
  const canvas = document.createElement("canvas");
  canvas.width = 720;
  canvas.height = 960;
  const context = canvas.getContext("2d");
  if (!context) return Promise.reject(new Error("Could not prepare the browser fixture image."));
  const background = context.createLinearGradient(0, 0, 720, 960);
  background.addColorStop(0, "#d8e7f5");
  background.addColorStop(1, "#f4dfd0");
  context.fillStyle = background;
  context.fillRect(0, 0, 720, 960);
  context.fillStyle = "#526783";
  context.beginPath();
  context.ellipse(360, 335, 156, 205, 0, 0, Math.PI * 2);
  context.fill();
  context.fillStyle = "#e1b99c";
  context.beginPath();
  context.ellipse(360, 330, 132, 176, 0, 0, Math.PI * 2);
  context.fill();
  context.fillStyle = "#34465e";
  context.beginPath();
  context.moveTo(130, 960);
  context.quadraticCurveTo(155, 630, 360, 620);
  context.quadraticCurveTo(565, 630, 590, 960);
  context.closePath();
  context.fill();
  context.fillStyle = "#f4f7fb";
  context.fillRect(270, 515, 180, 82);
  return new Promise((resolve, reject) => {
    canvas.toBlob(blob => {
      if (!blob) {
        reject(new Error("Could not encode the browser fixture image."));
        return;
      }
      resolve(new File([blob], "photo-studio-qa.png", { type: "image/png" }));
    }, "image/png");
  });
}

function PhotoStudioBrowserFixture() {
  const [source, setSource] = useState<File | null>(null);
  const [open, setOpen] = useState(false);
  const [result, setResult] = useState("");
  const [error, setError] = useState("");

  useEffect(() => {
    let active = true;
    void createFixturePhoto().then(file => {
      if (active) setSource(file);
    }).catch(reason => {
      if (active) setError(reason instanceof Error ? reason.message : String(reason));
    });
    return () => { active = false; };
  }, []);

  async function apply(sheets: PreparedPhotoSheet[]) {
    const sheet = sheets[0];
    setResult(JSON.stringify({
      mode,
      mediaType: sheet.file.type,
      fileName: sheet.file.name,
      sizeCode: sheet.sizeCode,
      photoCount: sheet.photoCount,
      bytes: sheet.file.size
    }));
    setOpen(false);
  }

  return <main style={{
    minHeight: "100dvh",
    display: "grid",
    placeItems: "center",
    padding: 24,
    background: dark ? "#101722" : "#f3f5fa",
    color: dark ? "#eef3fb" : "#26344b",
    fontFamily: "system-ui, sans-serif"
  }}>
    <section style={{ display: "grid", gap: 12, maxWidth: 520 }}>
      <h1 style={{ margin: 0, fontSize: 22 }}>Photo Studio QA fixture</h1>
      <p style={{ margin: 0, lineHeight: 1.6 }}>A synthetic test image is used. No customer document or external service is loaded.</p>
      <button id="open-photo-studio" type="button" disabled={!source} onClick={() => { setError(""); setOpen(true); }}>
        Open Photo Studio
      </button>
      {error && <p role="alert">{error}</p>}
      <output id="photo-studio-result" data-ready={Boolean(source)}>{result}</output>
    </section>
    {open && source && <PhotoStudio
      source={source}
      mode={mode}
      dark={dark}
      photoPrices={photoPrices}
      onCancel={() => setOpen(false)}
      onApply={apply}
    />}
  </main>;
}

createRoot(document.getElementById("photo-studio-qa-root")!).render(<PhotoStudioBrowserFixture />);
