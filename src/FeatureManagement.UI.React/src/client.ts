import type { AuditEntry, FeatureFlag, FeatureFlagAdminMetadata } from "./types";

export class FeatureFlagApiError extends Error {
  constructor(
    public status: number,
    public errors: string[],
  ) {
    super(errors.length ? errors.join(" ") : `Request failed (${status}).`);
  }
  get isConflict() {
    return this.status === 409;
  }
}

export interface FeatureFlagClientOptions {
  /** e.g. "/api/feature-flags" */
  baseUrl: string;
  /** Extra headers, e.g. Authorization. */
  headers?: () => Record<string, string> | Promise<Record<string, string>>;
  fetchImpl?: typeof fetch;
}

export function createFeatureFlagClient(options: FeatureFlagClientOptions) {
  const base = options.baseUrl.replace(/\/+$/, "");
  const doFetch = options.fetchImpl ?? ((...a: Parameters<typeof fetch>) => fetch(...a));

  async function send<T>(method: string, path: string, body?: unknown, etag?: string | null): Promise<T | undefined> {
    const headers: Record<string, string> = { ...(options.headers ? await options.headers() : {}) };
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (etag) headers["If-Match"] = `"${etag}"`;
    const res = await doFetch(`${base}${path}`, {
      method,
      headers,
      credentials: "include",
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!res.ok) {
      let errors: string[] = [];
      try {
        errors = ((await res.json()) as { errors?: string[] }).errors ?? [];
      } catch {
        // non-JSON error body
      }
      throw new FeatureFlagApiError(res.status, errors);
    }
    return res.status === 204 ? undefined : ((await res.json()) as T);
  }

  const enc = encodeURIComponent;
  return {
    list: async () => (await send<FeatureFlag[]>("GET", "")) ?? [],
    metadata: () => send<FeatureFlagAdminMetadata>("GET", "/meta"),
    audit: async () => (await send<AuditEntry[]>("GET", "/audit")) ?? [],
    create: (f: FeatureFlag) => send<FeatureFlag>("POST", "", f),
    update: (f: FeatureFlag) => send<FeatureFlag>("PUT", `/${enc(f.name)}`, f, f.eTag),
    remove: (f: FeatureFlag) => send<void>("DELETE", `/${enc(f.name)}`, undefined, f.eTag),
  };
}

export type FeatureFlagClient = ReturnType<typeof createFeatureFlagClient>;
