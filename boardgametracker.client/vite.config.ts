import { createHash } from "node:crypto";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { sentryVitePlugin } from "@sentry/vite-plugin";
import svgr from "vite-plugin-svgr";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { tanstackRouter } from "@tanstack/router-plugin/vite";

const localesPath = fileURLToPath(new URL("./public/locales", import.meta.url));

const getLocaleVersion = (root: string): string => {
  const hash = createHash("sha256");

  const visit = (directory: string) => {
    for (const entry of readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) {
        visit(path);
      } else if (entry.isFile() && entry.name.endsWith(".json")) {
        hash.update(path.slice(root.length));
        hash.update(readFileSync(path));
      }
    }
  };

  visit(root);
  return hash.digest("hex").slice(0, 12);
};

const localeVersion = getLocaleVersion(localesPath);

export default defineConfig({
  define: {
    __LOCALE_VERSION__: JSON.stringify(localeVersion),
  },
  plugins: [
    tanstackRouter({
      target: "react",
      autoCodeSplitting: true,
      routeFileIgnorePattern: ".test.",
    }),
    sentryVitePlugin({
      org: "boardgametracker",
      project: "boardgametracker",
    }),
    svgr(),
    react(),
  ],
  resolve: {
    tsconfigPaths: true,
  },
  base: "/",

  server: {
    port: Number(process.env.PORT) || 5443,
    strictPort: true,
    proxy: {
      "/api": {
        target: "http://localhost:6554/",
        changeOrigin: true,
        secure: false,
      },
      "/images/cover": {
        target: "http://localhost:6554/",
        changeOrigin: true,
        secure: false,
      },
      "/images/profile": {
        target: "http://localhost:6554/",
        changeOrigin: true,
        secure: false,
      },
    },
  },

  build: {
    sourcemap: true,
  },
});
