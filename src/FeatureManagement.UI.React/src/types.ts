export type FeatureKind = "ReleaseToggle" | "FeatureFlag";
export type ExpiryStatus = "None" | "Active" | "ExpiringSoon" | "Expired";
export type RequirementType = "Any" | "All";

export interface GroupRollout {
  name: string;
  rolloutPercentage: number;
}

export interface FeatureFlag {
  name: string;
  description?: string | null;
  enabled: boolean;
  kind: FeatureKind;
  owner?: string | null;
  createdUtc?: string | null;
  expiresUtc?: string | null;
  requirementType: RequirementType;
  percentage?: { value: number } | null;
  timeWindow?: { start?: string | null; end?: string | null } | null;
  targeting?: {
    users: string[];
    groups: GroupRollout[];
    defaultRolloutPercentage: number;
  } | null;
  otherFilters?: unknown[];
  eTag?: string | null;
  expiry?: ExpiryStatus;
}

export interface AuditEntry {
  timestampUtc: string;
  user: string;
  action: string;
  flagName: string;
}

export interface FeatureFlagAdminMetadata {
  releaseToggleDefaultLifetimeDays: number;
  featureFlagDefaultLifetimeDays: number | null;
  expiringSoonDays: number;
}
