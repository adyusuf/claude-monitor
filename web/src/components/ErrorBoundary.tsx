import { Component, type ErrorInfo, type ReactNode } from "react";
import { useI18n } from "../i18n";
import { Button, Empty } from "./ui";

/** The full reload, behind an object so a test can stand in for it (jsdom cannot reload). */
export const pageReload = { reload: () => window.location.reload() };

function PageError() {
  const { t } = useI18n();
  return (
    <div className="page">
      <Empty title={t("pageError.title")} hint={t("pageError.hint")}>
        <Button onClick={() => pageReload.reload()}>{t("pageError.reload")}</Button>
      </Empty>
    </div>
  );
}

/**
 * A page that throws while rendering (a null from an additive API change, say) shows a message instead of blanking the whole app;
 * the frame around it stays. Going to another page (`path` changes) clears it; a page that works is never remounted by it.
 */
export class ErrorBoundary extends Component<{ path: string; children: ReactNode }, { failed: boolean; path: string }> {
  constructor(props: { path: string; children: ReactNode }) {
    super(props);
    this.state = { failed: false, path: props.path };
  }

  static getDerivedStateFromError(): { failed: boolean } {
    return { failed: true };
  }

  static getDerivedStateFromProps(props: { path: string }, state: { path: string }): { failed: boolean; path: string } | null {
    return props.path !== state.path ? { failed: false, path: props.path } : null;
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error(error, info.componentStack);
  }

  render() {
    return this.state.failed ? <PageError /> : this.props.children;
  }
}
