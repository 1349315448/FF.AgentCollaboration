import { cp, mkdir, readdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { join } from "node:path";

const project = fileURLToPath(new URL("../../src/FF.AgentCollaboration/", import.meta.url));
const source = join(project, "wwwroot");
for (const config of ["Debug", "Release"]) {
  const target = join(project, "bin", config, "net10.0", "wwwroot");
  await mkdir(target, { recursive: true });
  // Publish the entry page last. Already-open pages can still request their original chunks.
  for (const entry of await readdir(source)) {
    if (entry !== "index.html") await cp(join(source, entry), join(target, entry), { recursive: true });
  }
  await cp(join(source, "index.html"), join(target, "index.html"));
}
