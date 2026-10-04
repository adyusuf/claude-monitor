import { Link, Navigate, Route, Routes } from "react-router";
import { RequireAuth, useSession } from "./auth/session";
import { Layout } from "./components/Layout";
import { Empty } from "./components/ui";
import { useI18n } from "./i18n";
import { AcceptInvitationPage, AccountPage, DownloadPage } from "./pages/AccountPages";
import { ForgotPage, LoginPage, RegisterPage, ResetPasswordPage, VerifyEmailPage } from "./pages/AuthPages";
import { DevicePage, MachinesPage } from "./pages/MachinesPage";
import { MembersPage } from "./pages/MembersPage";
import { MfaPage } from "./pages/Mfa";
import { SessionPage } from "./pages/SessionPage";
import { SessionsPage } from "./pages/SessionsPage";
import { NewWorkspacePage, WorkspaceSettingsPage } from "./pages/WorkspaceSettingsPage";

function Home() {
  const { me } = useSession();
  const first = me?.workspaces[0];
  return first ? <Navigate to={`/w/${first.id}/sessions`} replace /> : <Navigate to="/workspaces/new" replace />;
}

export function NotFound() {
  const { t } = useI18n();
  return <div className="page"><Empty title={t("notFound.title")}><Link className="btn btn-primary" to="/">{t("notFound.home")}</Link></Empty></div>;
}

/** Every page of the web app. The signed-out pages stand alone; the rest share the signed-in frame. */
export function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/verify-email" element={<VerifyEmailPage />} />
      <Route path="/forgot" element={<ForgotPage />} />
      <Route path="/reset-password" element={<ResetPasswordPage />} />
      <Route path="/mfa" element={<MfaPage />} />
      <Route element={<RequireAuth><Layout /></RequireAuth>}>
        <Route index element={<Home />} />
        <Route path="/settings" element={<AccountPage />} />
        <Route path="/account" element={<AccountPage />} />
        <Route path="/device" element={<DevicePage />} />
        <Route path="/download" element={<DownloadPage />} />
        <Route path="/invitations/accept" element={<AcceptInvitationPage />} />
        <Route path="/workspaces/new" element={<NewWorkspacePage />} />
        <Route path="/w/:ws/sessions" element={<SessionsPage />} />
        <Route path="/w/:ws/sessions/:id" element={<SessionPage />} />
        <Route path="/w/:ws/machines" element={<MachinesPage />} />
        <Route path="/w/:ws/members" element={<MembersPage />} />
        <Route path="/w/:ws/settings" element={<WorkspaceSettingsPage />} />
        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  );
}
