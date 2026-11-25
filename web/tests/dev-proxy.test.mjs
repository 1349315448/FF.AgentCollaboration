import assert from "node:assert/strict";
import { once } from "node:events";
import http from "node:http";
import test from "node:test";
import { createServer } from "vite";

test("HTTP and WebSocket proxy only forward requests from the local development origin", async () => {
  const origins = [];
  const backend = http.createServer((req, res) => {
    origins.push(req.headers.origin);
    res.writeHead(204).end();
  });
  backend.on("upgrade", (req, socket) => {
    origins.push(req.headers.origin);
    socket.end("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
  });
  backend.listen(0, "127.0.0.1");
  await once(backend, "listening");
  const target = "http://127.0.0.1:" + backend.address().port;
  const previous = process.env.FF_WORKBENCH_URL;
  process.env.FF_WORKBENCH_URL = target;
  let vite;
  try {
    vite = await createServer({ server:{port:0}, logLevel:"error", optimizeDeps:{noDiscovery:true} });
    await vite.listen();
    const host = "127.0.0.1:" + vite.httpServer.address().port;
    function status(upgrade, authority, origin) {
      const headers = { Host:authority, Origin:origin };
      if (upgrade) Object.assign(headers, { Connection:"Upgrade", Upgrade:"websocket" });
      return new Promise((resolve,reject) => {
        const req = http.get("http://" + host + "/api/probe", {headers}, res => { res.resume(); resolve(res.statusCode); });
        req.on("error",reject);
      });
    }
    for (const upgrade of [false,true]) {
      assert.equal(await status(upgrade,host,"http://" + host),204);
      assert.ok(await status(upgrade,host,"http://example.invalid") >= 400);
      assert.ok(await status(upgrade,"example.invalid","http://example.invalid") >= 400);
    }
    assert.deepEqual(origins,[target,target]);
  } finally {
    if (previous === undefined) delete process.env.FF_WORKBENCH_URL;
    else process.env.FF_WORKBENCH_URL = previous;
    await vite?.close();
    backend.closeAllConnections();
    await new Promise(resolve => backend.close(resolve));
  }
});
