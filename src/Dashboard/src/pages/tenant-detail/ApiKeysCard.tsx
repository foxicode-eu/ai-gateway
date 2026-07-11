import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api } from '@/lib/api'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from '@/components/ui/dialog'

export function ApiKeysCard({ tenantId }: { tenantId: string }) {
  const queryClient = useQueryClient()
  const keysQuery = useQuery({ queryKey: ['api-keys', tenantId], queryFn: () => api.listApiKeys(tenantId) })

  const [open, setOpen] = useState(false)
  const [name, setName] = useState('')
  const [newKey, setNewKey] = useState<string | null>(null)

  const createKey = useMutation({
    mutationFn: () => api.createApiKey(tenantId, name, null),
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ['api-keys', tenantId] })
      setNewKey(created.key)
      setName('')
    },
  })

  const revokeKey = useMutation({
    mutationFn: (apiKeyId: string) => api.revokeApiKey(tenantId, apiKeyId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['api-keys', tenantId] }),
  })

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    createKey.mutate()
  }

  function closeDialog(nextOpen: boolean) {
    setOpen(nextOpen)
    if (!nextOpen) {
      setNewKey(null)
    }
  }

  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between">
        <CardTitle>API keys</CardTitle>
        <Dialog open={open} onOpenChange={closeDialog}>
          <DialogTrigger asChild>
            <Button size="sm">New key</Button>
          </DialogTrigger>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>{newKey ? 'Key created' : 'Issue an API key'}</DialogTitle>
            </DialogHeader>
            {newKey ? (
              <div className="flex flex-col gap-3">
                <p className="text-sm text-muted-foreground">
                  This is the only time the plaintext key is shown — copy it now.
                </p>
                <code className="rounded bg-muted p-2 text-xs break-all">{newKey}</code>
                <DialogFooter>
                  <Button onClick={() => closeDialog(false)}>Done</Button>
                </DialogFooter>
              </div>
            ) : (
              <form onSubmit={handleSubmit} className="flex flex-col gap-4">
                <div className="flex flex-col gap-2">
                  <Label htmlFor="key-name">Name</Label>
                  <Input id="key-name" value={name} onChange={(e) => setName(e.target.value)} required autoFocus />
                </div>
                {createKey.isError && <p className="text-sm text-destructive">{(createKey.error as Error).message}</p>}
                <DialogFooter>
                  <Button type="submit" disabled={createKey.isPending || name.trim() === ''}>
                    {createKey.isPending ? 'Creating…' : 'Create'}
                  </Button>
                </DialogFooter>
              </form>
            )}
          </DialogContent>
        </Dialog>
      </CardHeader>
      <CardContent>
        {keysQuery.isLoading && <p className="text-sm text-muted-foreground">Loading…</p>}
        {keysQuery.data && keysQuery.data.length === 0 && (
          <p className="text-sm text-muted-foreground">No API keys yet.</p>
        )}
        {keysQuery.data && keysQuery.data.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Quota</TableHead>
                <TableHead>Created</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {keysQuery.data.map((key) => (
                <TableRow key={key.id}>
                  <TableCell className="font-medium">{key.name}</TableCell>
                  <TableCell>
                    {key.revokedAtUtc ? <Badge variant="destructive">Revoked</Badge> : <Badge variant="secondary">Active</Badge>}
                  </TableCell>
                  <TableCell>{key.tokenQuotaPerWindow ?? 'Unlimited'}</TableCell>
                  <TableCell>{new Date(key.createdAtUtc).toLocaleString()}</TableCell>
                  <TableCell>
                    {!key.revokedAtUtc && (
                      <Button
                        variant="ghost"
                        size="sm"
                        disabled={revokeKey.isPending}
                        onClick={() => revokeKey.mutate(key.id)}
                      >
                        Revoke
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  )
}
