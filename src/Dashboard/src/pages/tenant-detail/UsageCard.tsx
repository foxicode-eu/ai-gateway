import { useQuery } from '@tanstack/react-query'
import { Bar, BarChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'

import { api } from '@/lib/api'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'

export function UsageCard({ tenantId }: { tenantId: string }) {
  const usageQuery = useQuery({ queryKey: ['usage', tenantId], queryFn: () => api.getUsage(tenantId, 24) })

  const chartData =
    usageQuery.data?.byProvider.map((p) => ({
      provider: p.provider,
      'Prompt tokens': p.promptTokens,
      'Completion tokens': p.completionTokens,
    })) ?? []

  return (
    <Card>
      <CardHeader>
        <CardTitle>Usage (last 24h)</CardTitle>
        <CardDescription>
          {usageQuery.data &&
            `${usageQuery.data.totalRequests} requests · ${usageQuery.data.totalTokens} tokens · ${usageQuery.data.errorCount} errors`}
        </CardDescription>
      </CardHeader>
      <CardContent>
        {usageQuery.isLoading && <p className="text-sm text-muted-foreground">Loading…</p>}
        {usageQuery.data && usageQuery.data.totalRequests === 0 && (
          <p className="text-sm text-muted-foreground">No usage recorded yet.</p>
        )}
        {usageQuery.data && usageQuery.data.totalRequests > 0 && (
          <div className="h-64 w-full">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={chartData}>
                <CartesianGrid strokeDasharray="3 3" className="stroke-border" />
                <XAxis dataKey="provider" className="text-xs" />
                <YAxis className="text-xs" />
                <Tooltip />
                <Legend />
                <Bar dataKey="Prompt tokens" fill="#2563eb" />
                <Bar dataKey="Completion tokens" fill="#7c3aed" />
              </BarChart>
            </ResponsiveContainer>
          </div>
        )}
      </CardContent>
    </Card>
  )
}
