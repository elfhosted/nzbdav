import compression from "compression";
import express from "express";
import morgan from "morgan";
import http from "http";
import { WebSocketServer } from "ws";

// Short-circuit the type-checking of the built output.
const BUILD_PATH = "../build/server/index.js";
const DEVELOPMENT = process.env.NODE_ENV === "development";
const PORT = Number.parseInt(process.env.PORT || "3000");

// Keep the frontend alive when the backend is slow. The React Router SSR
// loaders fetch from the backend at localhost:8080 with a 10s undici timeout;
// when the backend is threadpool-starved, those fetches reject as
// ConnectTimeoutError and — if a loader doesn't catch them — the rejection
// propagates as an unhandledRejection, which Node escalates to an
// uncaughtException-from-promise and terminates the process for by default
// since v15. Logging-without-crashing keeps the rest of the frontend
// serving (404 routes, /assets, websocket, healthcheck) while the SSR pages
// just produce a 500 for the affected request. The alternative — the whole
// pod container exiting and Kubernetes restart-looping — is much worse for
// tenants.
//
// We deliberately don't hook uncaughtException: that fires for genuine
// fatal errors (port-in-use, failed module import, programmer mistakes)
// where the process is in an unknown state and Kubernetes restarting it is
// the right answer.
process.on("unhandledRejection", (reason) => {
  console.error("Unhandled promise rejection:", reason);
});

// Initialize the express app
const app = express();
app.use(
  compression({
    // Don't compress proxied WebDAV/media/API responses; keep Content-Length intact for seek
    filter: (req, res) => {
      const path = decodeURIComponent(req.path || "");
      if (
        path.startsWith("/view") ||
        path.startsWith("/.ids") ||
        path.startsWith("/nzbs") ||
        path.startsWith("/content") ||
        path.startsWith("/completed-symlinks") ||
        path.startsWith("/api")
      ) {
        return false;
      }
      return compression.filter(req, res);
    },
  }),
);
app.disable("x-powered-by");
app.set("trust proxy", true);

// Frontend-local healthcheck endpoint. Registered BEFORE the React Router
// catch-all so it bypasses SSR entirely — a probe hit doesn't depend on
// the backend being reachable, doesn't run loaders, doesn't trigger any
// fetch. Returns 200 as long as the Node process is alive and the express
// app is wired up. Without this, kubelet's startup probe hits "/" which
// goes through SSR; if the backend is slow, the loader's fetch times out
// at 10s, SSR returns 500, the probe fails, and Kubernetes kills the pod
// even though Node itself is fine.
app.get("/healthz", (_req, res) => {
  res.status(200).type("text/plain").send("ok");
});

// Custom morgan token: raw TCP peer (ignores trust-proxy / X-Forwarded-For).
morgan.token("socket-addr", (req) =>
  (req as any).socket?.remoteAddress
  ?? (req as any).connection?.remoteAddress
  ?? "-"
);
// Custom morgan token: full X-Forwarded-For chain as received (no rewriting).
morgan.token("xff", (req) => {
  const v = req.headers["x-forwarded-for"];
  return Array.isArray(v) ? v.join(",") : (v ?? "-");
});

// Initialize the websocket server as soon as both it and the server-module are ready
let _serverModule: any = null;
let _websocketServer: WebSocketServer | null = null;
const setWebsocketServer = (websocketServer: WebSocketServer) => {
  if (_websocketServer != null) return;
  if (_serverModule != null) _serverModule.initializeWebsocketServer(websocketServer);
  _websocketServer = websocketServer;
}
const setServerModule = (serverModule: any) => {
  if (_serverModule != null) return;
  if (_websocketServer != null) serverModule.initializeWebsocketServer(_websocketServer);
  _serverModule = serverModule;
}

// Handle development vs production
if (DEVELOPMENT) {
  console.log("Starting development server");
  const viteDevServer = await import("vite").then((vite) =>
    vite.createServer({
      server: { middlewareMode: true },
    }),
  );
  app.use(viteDevServer.middlewares);
  app.use(async (req, res, next) => {
    try {
      const serverModule = await viteDevServer.ssrLoadModule("./server/app.ts");
      setServerModule(serverModule);
      return await serverModule.app(req, res, next);
    } catch (error) {
      if (typeof error === "object" && error instanceof Error) {
        viteDevServer.ssrFixStacktrace(error);
      }
      next(error);
    }
  });
} else {
  console.log("Starting production server");
  app.use(
    "/assets",
    express.static("build/client/assets", { immutable: true, maxAge: "1y" }),
  );
  app.use(morgan(
    ':remote-addr (sock=:socket-addr xff=:xff) :method :url :status :res[content-length] - :response-time ms ":user-agent"',
    {
      skip: (req, res) => {
        return res.statusCode < 400
          || req.url === "/favicon.ico"
      }
    }
  ));
  app.use(express.static("build/client", { maxAge: "1h" }));
  const serverModule = await import(BUILD_PATH);
  app.use(serverModule.app);
  setServerModule(serverModule);
}

// Create both the http and websocket servers
const server = http.createServer(app);
setWebsocketServer(new WebSocketServer({ server }));

// Begin listening for connections
server.listen(PORT, () => {
  console.log(`Server is running on http://localhost:${PORT}`);
});
