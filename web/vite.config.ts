import { defineConfig, type ProxyOptions } from "vite";

// Dev traffic follows the same loopback boundary as the Kestrel host.
const target = new URL(process.env.FF_WORKBENCH_URL || "http://127.0.0.1:5186");
if (target.protocol !== "http:" || !["127.0.0.1","localhost","[::1]"].includes(target.hostname)) {
  throw new Error("FF_WORKBENCH_URL must be a loopback HTTP endpoint");
}
target.hostname = "127.0.0.1";
const proxy: ProxyOptions = {
  target: target.origin, changeOrigin: true, ws: true,
  bypass(request) {
    const port = request.socket.localPort;
    const host = request.headers.host;
    if (host !== "127.0.0.1:" + port && host !== "localhost:" + port) return false;
    if (request.headers.origin && request.headers.origin !== "http://" + host) return false;
  },
  configure(server) {
    const forwardOrigin = (outgoing: import("node:http").ClientRequest, request: import("node:http").IncomingMessage) => {
      if (request.headers.origin === "http://" + request.headers.host) outgoing.setHeader("Origin", target.origin);
    };
    server.on("proxyReq", forwardOrigin);
    server.on("proxyReqWs", forwardOrigin);
  }
};
export default defineConfig({
  server: { host: "127.0.0.1", proxy: { "/api": proxy, "/ws": proxy } },
  build: { outDir: "../src/FF.AgentCollaboration/wwwroot", emptyOutDir: true }
});
