import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api } from '@/lib/api'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

export function ProvidersCard({ tenantId }: { tenantId: string }) {
  const queryClient = useQueryClient()
  const providersQuery = useQuery({ queryKey: ['providers', tenantId], queryFn: () => api.listProviders(tenantId) })
  const [editingProvider, setEditingProvider] = useState<string | null>(null)
  const [credential, setCredential] = useState('')

  const setCredentialMutation = useMutation({
    mutationFn: () => api.setProviderCredential(tenantId, editingProvider!, credential),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['providers', tenantId] })
      setEditingProvider(null)
      setCredential('')
    },
  })

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setCredentialMutation.mutate()
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Provider credentials (BYOK)</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        {providersQuery.isLoading && <p className="text-sm text-muted-foreground">Loading…</p>}
        {providersQuery.data?.map((provider) => (
          <div key={provider.provider} className="flex flex-col gap-2 rounded-md border p-3">
            <div className="flex items-center justify-between">
              <span className="font-medium capitalize">{provider.provider}</span>
              <div className="flex items-center gap-2">
                {provider.configured ? (
                  <Badge variant="secondary">Configured</Badge>
                ) : (
                  <Badge variant="outline">Not configured</Badge>
                )}
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => {
                    setEditingProvider(provider.provider)
                    setCredential('')
                  }}
                >
                  {provider.configured ? 'Replace key' : 'Set key'}
                </Button>
              </div>
            </div>
            {editingProvider === provider.provider && (
              <form onSubmit={handleSubmit} className="flex items-center gap-2">
                <Input
                  value={credential}
                  onChange={(e) => setCredential(e.target.value)}
                  placeholder={`${provider.provider} API key`}
                  autoFocus
                  required
                />
                <Button type="submit" size="sm" disabled={setCredentialMutation.isPending}>
                  Save
                </Button>
                <Button type="button" variant="ghost" size="sm" onClick={() => setEditingProvider(null)}>
                  Cancel
                </Button>
              </form>
            )}
          </div>
        ))}
      </CardContent>
    </Card>
  )
}
