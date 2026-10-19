import { StrictMode, type ComponentType } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import "./styles/global.css";

async function start() {
  let RootComponent: ComponentType = App;

  if (new URLSearchParams(window.location.search).get("panel") === "1") {
    const panel = await import("./panel/Panel");
    RootComponent = panel.Panel;
  }

  if (
    import.meta.env.DEV &&
    new URLSearchParams(window.location.search).get("simulator") === "1"
  ) {
    const simulator = await import("./dev/SimulatorApp");
    RootComponent = simulator.SimulatorApp;
  }

  createRoot(document.getElementById("root")!).render(
    <StrictMode>
      <RootComponent />
    </StrictMode>,
  );
}

void start();
