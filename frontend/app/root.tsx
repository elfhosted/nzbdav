import {
  isRouteErrorResponse,
  Links,
  Meta,
  Outlet,
  Scripts,
  ScrollRestoration,
  useLocation,
  useNavigation,
  useRouteError,
} from "react-router";

import 'bootstrap/dist/css/bootstrap.min.css';
import "./app.css";
import type { Route } from "./+types/root";
import { IS_FRONTEND_AUTH_DISABLED } from "~/auth/authentication.server";
import { TopNavigation } from "./routes/_index/components/top-navigation/top-navigation";
import { LeftNavigation } from "./routes/_index/components/left-navigation/left-navigation";
import { PageLayout } from "./routes/_index/components/page-layout/page-layout";
import { Loading } from "./routes/_index/components/loading/loading";

export async function loader({ request }: Route.LoaderArgs) {
  let path = new URL(request.url).pathname;
  if (path === "/login") return { useLayout: false };
  if (path === "/onboarding") return { useLayout: false };

  return {
    useLayout: true,
    version: process.env.NZBDAV_VERSION,
    isFrontendAuthDisabled: IS_FRONTEND_AUTH_DISABLED,
  };
}


export function Layout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" data-bs-theme="dark">
      <head>
        <meta charSet="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <link rel="icon" href="/logo.svg" />
        <Meta />
        <Links />
      </head>
      <body>
        {children}
        <ScrollRestoration />
        <Scripts />
      </body>
    </html>
  );
}

export default function App({ loaderData }: Route.ComponentProps) {
  const { useLayout, version, isFrontendAuthDisabled } = loaderData;
  const location = useLocation();
  const navigation = useNavigation();
  const isNavigating = Boolean(navigation.location);

  // display loading animiation during top-level page transitions,
  // but allow the `/explore` page to handle it's own loading screen.
  const isCurrentExplorePage = location.pathname.startsWith("/explore");
  const isNextExplorePage = navigation.location?.pathname?.startsWith("/explore");
  const showLoading = isNavigating && !(isCurrentExplorePage && isNextExplorePage);

  if (useLayout) {
    return (
      <PageLayout
        topNavComponent={TopNavigation}
        bodyChild={showLoading ? <Loading /> : <Outlet />}
        leftNavChild={
          <LeftNavigation
            version={version}
            isFrontendAuthDisabled={isFrontendAuthDisabled} />
        } />
    );
  }

  return <Outlet />;
}

// Root ErrorBoundary catches anything a route loader / component throws that
// isn't caught by a closer error boundary. Without this, an SSR loader that
// rejects (the common case: backend fetch hits its 10s undici timeout while
// the backend container is busy) bubbles up to React Router's default error
// renderer, which returns a 500 with no UI — making the WebUI completely
// inaccessible whenever the backend is degraded.
//
// We render a minimal HTML shell here rather than reusing PageLayout: the
// layout's left-nav loader would re-run and hit the same backend, looping us
// back into the error. A flat informational page lets the user see *why* the
// site isn't loading, hit refresh, and otherwise wait it out.
export function ErrorBoundary() {
  const error = useRouteError();
  const isUndiciTimeout =
    error instanceof Error
    && /fetch failed|ConnectTimeoutError|UND_ERR_CONNECT_TIMEOUT/i.test(
      `${error.message} ${(error.cause as Error)?.message ?? ""}`
    );

  let title = "Something went wrong";
  let detail: string;
  if (isUndiciTimeout) {
    title = "Backend temporarily unavailable";
    detail =
      "The nzbdav backend is still starting up or is busy processing a "
      + "large queue. Wait a moment and refresh the page.";
  } else if (isRouteErrorResponse(error)) {
    title = `${error.status} ${error.statusText}`;
    detail = typeof error.data === "string" ? error.data : "";
  } else if (error instanceof Error) {
    detail = error.message;
  } else {
    detail = "Unknown error.";
  }

  return (
    <div style={{
      padding: "2rem",
      maxWidth: "640px",
      margin: "4rem auto",
      fontFamily: "system-ui, sans-serif",
    }}>
      <h2 style={{ marginBottom: "0.5rem" }}>{title}</h2>
      <p style={{ marginBottom: "1.5rem", lineHeight: 1.5 }}>{detail}</p>
      <button
        type="button"
        onClick={() => window.location.reload()}
        style={{
          padding: "0.5rem 1rem",
          borderRadius: "4px",
          border: "1px solid #888",
          background: "transparent",
          color: "inherit",
          cursor: "pointer",
        }}
      >
        Reload
      </button>
    </div>
  );
}