import { useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'

import { api, type Tenant } from '@/lib/api'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

export function QuotaCard({ tenant }: { tenant: Tenant }) {
  const queryClient = useQueryClient()
  const [quota, setQuota] = useState(tenant.tokenQuotaPerWindow?.toString() ?? '')

  const updateQuota = useMutation({
    mutationFn: () => api.updateTenant(tenant.id, quota.trim() === '' ? null : Number(quota)),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['tenant', tenant.id] }),
  })

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    updateQuota.mutate()
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Token quota</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit} className="flex items-end gap-3">
          <div className="flex flex-col gap-2">
            <Label htmlFor="quota">Tokens per window (blank = unlimited)</Label>
            <Input
              id="quota"
              type="number"
              min={0}
              value={quota}
              onChange={(e) => setQuota(e.target.value)}
              placeholder="unlimited"
              className="w-56"
            />
          </div>
          <Button type="submit" disabled={updateQuota.isPending}>
            {updateQuota.isPending ? 'Saving…' : 'Save'}
          </Button>
          {updateQuota.isSuccess && <span className="text-sm text-muted-foreground">Saved.</span>}
        </form>
      </CardContent>
    </Card>
  )
}
