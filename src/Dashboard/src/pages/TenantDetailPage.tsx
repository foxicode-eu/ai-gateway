import { Link } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'

import { api } from '@/lib/api'
import { QuotaCard } from '@/pages/tenant-detail/QuotaCard'
import { ApiKeysCard } from '@/pages/tenant-detail/ApiKeysCard'
import { ProvidersCard } from '@/pages/tenant-detail/ProvidersCard'
import { UsageCard } from '@/pages/tenant-detail/UsageCard'

export function TenantDetailPage({ tenantId }: { tenantId: string }) {
  const tenantQuery = useQuery({ queryKey: ['tenant', tenantId], queryFn: () => api.getTenant(tenantId) })

  if (tenantQuery.isLoading) {
    return <p className="text-sm text-muted-foreground">Loading…</p>
  }

  if (tenantQuery.isError || !tenantQuery.data) {
    return <p className="text-sm text-destructive">Tenant not found.</p>
  }

  const tenant = tenantQuery.data

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link to="/" className="text-sm text-muted-foreground hover:underline">
          ← All tenants
        </Link>
        <h1 className="text-2xl font-semibold">{tenant.name}</h1>
        <p className="text-sm text-muted-foreground">{tenant.id}</p>
      </div>

      <QuotaCard tenant={tenant} />
      <ApiKeysCard tenantId={tenant.id} />
      <ProvidersCard tenantId={tenant.id} />
      <UsageCard tenantId={tenant.id} />
    </div>
  )
}
