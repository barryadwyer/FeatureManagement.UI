import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Badge,
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Switch,
  Tab,
  TabList,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Textarea,
  tokens,
} from "@fluentui/react-components";
import {
  createFeatureFlagClient,
  FeatureFlagApiError,
  type FeatureFlagClient,
  type FeatureFlagClientOptions,
} from "./client";
import type {
  AuditEntry,
  ExpiryStatus,
  FeatureFlag,
  FeatureFlagAdminMetadata,
  FeatureKind,
  GroupRollout,
} from "./types";

export interface FeatureFlagAdminProps extends Partial<FeatureFlagClientOptions> {
  /** Provide a pre-built client instead of baseUrl. */
  client?: FeatureFlagClient;
}

interface FormState {
  isNew: boolean;
  original?: FeatureFlag;
  name: string;
  description: string;
  enabled: boolean;
  kind: FeatureKind;
  owner: string;
  expires: string;
  requirementType: "Any" | "All";
  usePercentage: boolean;
  percentage: string;
  useTimeWindow: boolean;
  start: string;
  end: string;
  useTargeting: boolean;
  users: string;
  groups: string;
  defaultRollout: string;
}

const day = (iso?: string | null) => (iso ? iso.slice(0, 10) : "");
const toIso = (d: string) => (d ? `${d}T00:00:00Z` : null);

function newForm(kind: FeatureKind, meta?: FeatureFlagAdminMetadata): FormState {
  const days = kind === "ReleaseToggle" ? meta?.releaseToggleDefaultLifetimeDays ?? 90 : meta?.featureFlagDefaultLifetimeDays;
  const expires = days ? new Date(Date.now() + days * 86_400_000).toISOString().slice(0, 10) : "";
  return {
    isNew: true, name: "", description: "", enabled: false, kind, owner: "", expires,
    requirementType: "Any", usePercentage: false, percentage: "50", useTimeWindow: false, start: "", end: "",
    useTargeting: false, users: "", groups: "", defaultRollout: "0",
  };
}

function formFrom(f: FeatureFlag): FormState {
  return {
    isNew: false, original: f, name: f.name, description: f.description ?? "", enabled: f.enabled, kind: f.kind,
    owner: f.owner ?? "", expires: day(f.expiresUtc), requirementType: f.requirementType,
    usePercentage: !!f.percentage, percentage: String(f.percentage?.value ?? 50),
    useTimeWindow: !!f.timeWindow, start: day(f.timeWindow?.start), end: day(f.timeWindow?.end),
    useTargeting: !!f.targeting, users: (f.targeting?.users ?? []).join("\n"),
    groups: (f.targeting?.groups ?? []).map((g) => `${g.name}:${g.rolloutPercentage}`).join("\n"),
    defaultRollout: String(f.targeting?.defaultRolloutPercentage ?? 0),
  };
}

function parseGroups(text: string): GroupRollout[] {
  return text.split(/\r?\n/).map((l) => l.trim()).filter(Boolean).map((l) => {
    const i = l.lastIndexOf(":");
    return i < 0 ? { name: l, rolloutPercentage: 100 } : { name: l.slice(0, i).trim(), rolloutPercentage: Number(l.slice(i + 1)) || 0 };
  });
}

function toFlag(s: FormState): FeatureFlag {
  return {
    ...(s.original ?? {}),
    name: s.name.trim(),
    description: s.description || null,
    enabled: s.enabled,
    kind: s.kind,
    owner: s.owner || null,
    expiresUtc: toIso(s.expires),
    requirementType: s.requirementType,
    percentage: s.usePercentage ? { value: Number(s.percentage) || 0 } : null,
    timeWindow: s.useTimeWindow ? { start: toIso(s.start), end: toIso(s.end) } : null,
    targeting: s.useTargeting
      ? {
          users: s.users.split(/\r?\n/).map((u) => u.trim()).filter(Boolean),
          groups: parseGroups(s.groups),
          defaultRolloutPercentage: Number(s.defaultRollout) || 0,
        }
      : null,
  };
}

function describe(f: FeatureFlag): string {
  const parts: string[] = [];
  if (f.percentage) parts.push(`${f.percentage.value}% of users`);
  if (f.timeWindow) parts.push(`${day(f.timeWindow.start) || "…"} to ${day(f.timeWindow.end) || "…"}`);
  if (f.targeting)
    parts.push(`${f.targeting.users.length} users, ${f.targeting.groups.length} groups, ${f.targeting.defaultRolloutPercentage}% default`);
  if (f.otherFilters?.length) parts.push(`${f.otherFilters.length} custom`);
  return parts.length ? parts.join("; ") : "None";
}

const badgeColor = (s?: ExpiryStatus) => (s === "Expired" ? "danger" : s === "ExpiringSoon" ? "warning" : "success");

export function FeatureFlagAdmin(props: FeatureFlagAdminProps) {
  const { client: provided, ...clientOptions } = props;
  const client = useMemo(
    () => provided ?? createFeatureFlagClient({ baseUrl: clientOptions.baseUrl ?? "/api/feature-flags", ...clientOptions }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [provided, clientOptions.baseUrl],
  );

  const [flags, setFlags] = useState<FeatureFlag[]>([]);
  const [audit, setAudit] = useState<AuditEntry[]>([]);
  const [meta, setMeta] = useState<FeatureFlagAdminMetadata>();
  const [tab, setTab] = useState<"ReleaseToggle" | "FeatureFlag" | "audit">("ReleaseToggle");
  const [form, setForm] = useState<FormState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);

  const guard = useCallback(async <T,>(action: () => Promise<T>): Promise<T | undefined> => {
    try {
      return await action();
    } catch (e) {
      if (e instanceof FeatureFlagApiError && e.isConflict) setError(`${e.message} Refresh and try again.`);
      else setError(e instanceof Error ? e.message : String(e));
      return undefined;
    }
  }, []);

  const reload = useCallback(async () => {
    setLoading(true);
    setError(null);
    const f = await guard(() => client.list());
    if (f) setFlags(f);
    const a = await guard(() => client.audit());
    if (a) setAudit(a);
    setLoading(false);
  }, [client, guard]);

  useEffect(() => {
    void guard(() => client.metadata()).then((m) => m && setMeta(m));
    void reload();
  }, [client, guard, reload]);

  const patch = (p: Partial<FormState>) => setForm((f) => (f ? { ...f, ...p } : f));

  const save = async () => {
    if (!form) return;
    setBusy(true);
    const flag = toFlag(form);
    const ok = await guard(async () => {
      await (form.isNew ? client.create(flag) : client.update(flag));
      return true;
    });
    setBusy(false);
    if (ok) {
      setForm(null);
      await reload();
    }
  };

  const toggle = async (f: FeatureFlag, enabled: boolean) => {
    if (await guard(async () => (await client.update({ ...f, enabled }), true))) await reload();
  };

  const remove = async (f: FeatureFlag) => {
    if (await guard(async () => (await client.remove(f), true))) await reload();
  };

  const rows = flags.filter((f) => f.kind === tab);
  const count = (k: FeatureKind) => flags.filter((f) => f.kind === k).length;

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
        <Text size={600} weight="semibold">Feature flags</Text>
        <div style={{ display: "flex", gap: 8 }}>
          <Button onClick={() => setForm(newForm("ReleaseToggle", meta))}>New release toggle</Button>
          <Button onClick={() => setForm(newForm("FeatureFlag", meta))}>New feature flag</Button>
          <Button onClick={() => void reload()}>Refresh</Button>
        </div>
      </div>

      {error && (
        <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>
      )}

      <div
        style={{
          border: `1px solid ${tokens.colorNeutralStroke1}`,
          borderRadius: tokens.borderRadiusMedium,
          background: tokens.colorNeutralBackground2,
          padding: "4px 12px",
        }}
      >
        <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as typeof tab)}>
          <Tab value="ReleaseToggle">Release toggles ({count("ReleaseToggle")})</Tab>
          <Tab value="FeatureFlag">Feature flags ({count("FeatureFlag")})</Tab>
          <Tab value="audit">Audit log</Tab>
        </TabList>
      </div>

      {loading ? (
        <Spinner />
      ) : tab === "audit" ? (
        <Table aria-label="Audit log">
          <TableHeader>
            <TableRow>
              <TableHeaderCell>When (UTC)</TableHeaderCell>
              <TableHeaderCell>User</TableHeaderCell>
              <TableHeaderCell>Action</TableHeaderCell>
              <TableHeaderCell>Flag</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {audit.map((a, i) => (
              <TableRow key={i}>
                <TableCell>{a.timestampUtc}</TableCell>
                <TableCell>{a.user}</TableCell>
                <TableCell>{a.action}</TableCell>
                <TableCell>{a.flagName}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : (
        <Table aria-label={tab === "FeatureFlag" ? "Feature flags" : "Release toggles"}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>Name</TableHeaderCell>
              <TableHeaderCell>Enabled</TableHeaderCell>
              {tab === "FeatureFlag" && <TableHeaderCell>Filters</TableHeaderCell>}
              <TableHeaderCell>Owner</TableHeaderCell>
              <TableHeaderCell>Expires</TableHeaderCell>
              <TableHeaderCell />
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((f) => (
              <TableRow key={f.name}>
                <TableCell>
                  <strong>{f.name}</strong>
                  {f.description && <div>{f.description}</div>}
                </TableCell>
                <TableCell>
                  <Switch checked={f.enabled} onChange={(_, d) => void toggle(f, d.checked)} aria-label={`Enable ${f.name}`} />
                </TableCell>
                {tab === "FeatureFlag" && <TableCell>{describe(f)}</TableCell>}
                <TableCell>{f.owner}</TableCell>
                <TableCell>
                  {f.expiresUtc ? (
                    <Badge color={badgeColor(f.expiry)} appearance="tint">
                      {day(f.expiresUtc)}
                      {f.expiry === "Expired" ? " (expired)" : f.expiry === "ExpiringSoon" ? " (soon)" : ""}
                    </Badge>
                  ) : (
                    "Never"
                  )}
                </TableCell>
                <TableCell>
                  <Button size="small" onClick={() => setForm(formFrom(f))}>Edit</Button>{" "}
                  <Button size="small" onClick={() => void remove(f)}>Delete</Button>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}

      <Dialog open={form !== null} onOpenChange={(_, d) => !d.open && setForm(null)}>
        <DialogSurface>
          {form && (
            <DialogBody>
              <DialogTitle>
                {form.isNew ? "New" : "Edit"} {form.kind === "ReleaseToggle" ? "release toggle" : "feature flag"}
              </DialogTitle>
              <DialogContent style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                <Field label="Name"><Input value={form.name} disabled={!form.isNew} onChange={(_, d) => patch({ name: d.value })} /></Field>
                <Field label="Description"><Textarea value={form.description} onChange={(_, d) => patch({ description: d.value })} /></Field>
                <Switch label="Enabled" checked={form.enabled} onChange={(_, d) => patch({ enabled: d.checked })} />
                <Field label="Type">
                  <Select value={form.kind} onChange={(_, d) => patch({ kind: d.value as FeatureKind })}>
                    <option value="ReleaseToggle">Release toggle (short-lived)</option>
                    <option value="FeatureFlag">Feature flag (long-lived)</option>
                  </Select>
                </Field>
                <Field label="Owner"><Input value={form.owner} onChange={(_, d) => patch({ owner: d.value })} /></Field>
                <Field label="Expires (UTC date, empty for none)">
                  <Input type="date" value={form.expires} onChange={(_, d) => patch({ expires: d.value })} />
                </Field>

                {form.kind === "FeatureFlag" && (
                  <>
                <Text weight="semibold">Filters</Text>
                <Field label="Combine filters">
                  <Select value={form.requirementType} onChange={(_, d) => patch({ requirementType: d.value as "Any" | "All" })}>
                    <option value="Any">Any may match</option>
                    <option value="All">All must match</option>
                  </Select>
                </Field>

                <Checkbox label="Percentage" checked={form.usePercentage} onChange={(_, d) => patch({ usePercentage: !!d.checked })} />
                {form.usePercentage && (
                  <Field label="Enabled for % of users">
                    <Input type="number" min={0} max={100} value={form.percentage} onChange={(_, d) => patch({ percentage: d.value })} />
                  </Field>
                )}

                <Checkbox label="Time window" checked={form.useTimeWindow} onChange={(_, d) => patch({ useTimeWindow: !!d.checked })} />
                {form.useTimeWindow && (
                  <>
                    <Field label="Start (UTC date)"><Input type="date" value={form.start} onChange={(_, d) => patch({ start: d.value })} /></Field>
                    <Field label="End (UTC date)"><Input type="date" value={form.end} onChange={(_, d) => patch({ end: d.value })} /></Field>
                  </>
                )}

                <Checkbox label="Targeting (users and groups)" checked={form.useTargeting} onChange={(_, d) => patch({ useTargeting: !!d.checked })} />
                {form.useTargeting && (
                  <>
                    <Field label="Users (one per line)"><Textarea value={form.users} onChange={(_, d) => patch({ users: d.value })} /></Field>
                    <Field label="Groups (one per line as name:percent)"><Textarea value={form.groups} onChange={(_, d) => patch({ groups: d.value })} /></Field>
                    <Field label="Default rollout %">
                      <Input type="number" min={0} max={100} value={form.defaultRollout} onChange={(_, d) => patch({ defaultRollout: d.value })} />
                    </Field>
                  </>
                )}
                  </>
                )}
              </DialogContent>
              <DialogActions>
                <Button appearance="primary" disabled={busy} onClick={() => void save()}>Save</Button>
                <Button onClick={() => setForm(null)}>Cancel</Button>
              </DialogActions>
            </DialogBody>
          )}
        </DialogSurface>
      </Dialog>
    </div>
  );
}
