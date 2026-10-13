import { createRoot } from "react-dom/client";
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { FeatureFlagAdmin } from "@featuremanagement/ui-react";

createRoot(document.getElementById("root")!).render(
  <FluentProvider theme={webLightTheme} style={{ minHeight: "100vh", padding: 24 }}>
    <FeatureFlagAdmin baseUrl="/api/feature-flags" />
  </FluentProvider>,
);
