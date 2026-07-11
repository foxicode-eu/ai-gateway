import { useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'

import { api, type Tenant } from '@/lib/api'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card'

export function QuotaCard({ tenant }: { tenant: Tenant }) {
  const queryClient = useQueryClient()
  const [quota, setQuota] = useState(tenant.tokenQuotaPerWindow?.toString() ?? '')
  const [webhookUrl, setWebhookUrl] = useState(tenant.alertWebhookUrl ?? '')
  const [thresholds, setThresholds] = useState(tenant.alertThresholdPercentages?.join(', ') ?? '')

  const updateQuota = useMutation({
    mutationFn: () =>
      api.updateTenant(
        tenant.id,
        quota.trim() === '' ? null : Number(quota),
        webhookUrl.trim() === '' ? null : webhookUrl.trim(),
        thresholds.trim() === ''
          ? null
          : thresholds
              .split(',')
              .map((t) => Number(t.trim()))
              .filter((t) => !Number.isNaN(t)),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['tenant', tenant.id] }),
  })

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    updateQuota.mutate()
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Token quota & alerts</CardTitle>
        <CardDescription>
          Alert thresholds fire a webhook once usage crosses that percentage of the token quota within a window.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit} className="flex flex-col gap-4">
          <div className="flex items-end gap-3">
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
          </div>
          <div className="flex flex-wrap items-end gap-3">
            <div className="flex flex-col gap-2">
              <Label htmlFor="webhook-url">Alert webhook URL</Label>
              <Input
                id="webhook-url"
                type="url"
                value={webhookUrl}
                onChange={(e) => setWebhookUrl(e.target.value)}
                placeholder="https://example.com/hooks/quota"
                className="w-80"
              />
            </div>
            <div className="flex flex-col gap-2">
              <Label htmlFor="thresholds">Thresholds % (comma-separated)</Label>
              <Input
                id="thresholds"
                value={thresholds}
                onChange={(e) => setThresholds(e.target.value)}
                placeholder="80, 100"
                className="w-40"
              />
            </div>
          </div>
          <div className="flex items-center gap-3">
            <Button type="submit" disabled={updateQuota.isPending}>
              {updateQuota.isPending ? 'Saving…' : 'Save'}
            </Button>
            {updateQuota.isSuccess && <span className="text-sm text-muted-foreground">Saved.</span>}
          </div>
        </form>
      </CardContent>
    </Card>
  )
}
