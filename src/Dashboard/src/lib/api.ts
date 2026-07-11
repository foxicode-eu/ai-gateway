// Thin fetch wrapper around Management's REST API. Always sends credentials (the session cookie — see
// Management's /auth/login) and always hits /api/*, which vite.config.ts proxies to Management in dev so this
// is same-origin from the browser's perspective. A production build would need an equivalent same-origin proxy
// (or Management's CORS config) in front — see CLAUDE.md's Dashboard section.

export class ApiError extends Error {
  status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, {
    ...init,
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      ...init?.headers,
    },
  })

  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new ApiError(response.status, body?.error?.message ?? response.statusText)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return (await response.json()) as T
}

export interface Tenant {
  id: string
  name: string
  createdAtUtc: string
  tokenQuotaPerWindow: number | null
}

export interface ApiKeySummary {
  id: string
  name: string
  createdAtUtc: string
  revokedAtUtc: string | null
  tokenQuotaPerWindow: number | null
}

export interface ApiKeyCreated extends ApiKeySummary {
  key: string
}

export interface ProviderStatus {
  provider: string
  configured: boolean
}

export interface UsageSummary {
  tenantId: string
  sinceUtc: string
  untilUtc: string
  totalRequests: number
  totalPromptTokens: number
  totalCompletionTokens: number
  totalTokens: number
  errorCount: number
  byProvider: { provider: string; requests: number; promptTokens: number; completionTokens: number }[]
}

export const api = {
  login: (token: string) => request<{ authenticated: boolean }>('/auth/login', { method: 'POST', body: JSON.stringify({ token }) }),
  logout: () => request<void>('/auth/logout', { method: 'POST' }),
  session: () => request<{ authenticated: boolean }>('/auth/session'),

  listTenants: () => request<Tenant[]>('/tenants'),
  getTenant: (tenantId: string) => request<Tenant>(`/tenants/${tenantId}`),
  createTenant: (name: string, tokenQuotaPerWindow: number | null) =>
    request<Tenant>('/tenants', { method: 'POST', body: JSON.stringify({ name, tokenQuotaPerWindow }) }),
  updateTenant: (tenantId: string, tokenQuotaPerWindow: number | null) =>
    request<Tenant>(`/tenants/${tenantId}`, { method: 'PATCH', body: JSON.stringify({ tokenQuotaPerWindow }) }),

  listApiKeys: (tenantId: string) => request<ApiKeySummary[]>(`/tenants/${tenantId}/api-keys`),
  createApiKey: (tenantId: string, name: string, tokenQuotaPerWindow: number | null) =>
    request<ApiKeyCreated>(`/tenants/${tenantId}/api-keys`, {
      method: 'POST',
      body: JSON.stringify({ name, tokenQuotaPerWindow }),
    }),
  revokeApiKey: (tenantId: string, apiKeyId: string) =>
    request<void>(`/tenants/${tenantId}/api-keys/${apiKeyId}`, { method: 'DELETE' }),

  listProviders: (tenantId: string) => request<ProviderStatus[]>(`/tenants/${tenantId}/providers`),
  setProviderCredential: (tenantId: string, provider: string, apiKey: string) =>
    request<void>(`/tenants/${tenantId}/providers/${provider}`, { method: 'PUT', body: JSON.stringify({ apiKey }) }),

  getUsage: (tenantId: string, sinceHours = 24) =>
    request<UsageSummary>(`/tenants/${tenantId}/usage?sinceHours=${sinceHours}`),
}
