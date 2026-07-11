import { useState, type FormEvent } from 'react'
import { Link } from '@tanstack/react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api } from '@/lib/api'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from '@/components/ui/dialog'

export function TenantsListPage() {
  const queryClient = useQueryClient()
  const tenantsQuery = useQuery({ queryKey: ['tenants'], queryFn: api.listTenants })

  const [open, setOpen] = useState(false)
  const [name, setName] = useState('')
  const [quota, setQuota] = useState('')

  const createTenant = useMutation({
    mutationFn: () => api.createTenant(name, quota.trim() === '' ? null : Number(quota)),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['tenants'] })
      setOpen(false)
      setName('')
      setQuota('')
    },
  })

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    createTenant.mutate()
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-semibold">Tenants</h1>
        <Dialog open={open} onOpenChange={setOpen}>
          <DialogTrigger asChild>
            <Button>New tenant</Button>
          </DialogTrigger>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Create a tenant</DialogTitle>
            </DialogHeader>
            <form onSubmit={handleSubmit} className="flex flex-col gap-4">
              <div className="flex flex-col gap-2">
                <Label htmlFor="name">Name</Label>
                <Input id="name" value={name} onChange={(e) => setName(e.target.value)} required autoFocus />
              </div>
              <div className="flex flex-col gap-2">
                <Label htmlFor="quota">Token quota per window (blank = unlimited)</Label>
                <Input
                  id="quota"
                  type="number"
                  min={0}
                  value={quota}
                  onChange={(e) => setQuota(e.target.value)}
                  placeholder="unlimited"
                />
              </div>
              {createTenant.isError && <p className="text-sm text-destructive">{(createTenant.error as Error).message}</p>}
              <DialogFooter>
                <Button type="submit" disabled={createTenant.isPending || name.trim() === ''}>
                  {createTenant.isPending ? 'Creating…' : 'Create'}
                </Button>
              </DialogFooter>
            </form>
          </DialogContent>
        </Dialog>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>All tenants</CardTitle>
        </CardHeader>
        <CardContent>
          {tenantsQuery.isLoading && <p className="text-sm text-muted-foreground">Loading…</p>}
          {tenantsQuery.isError && <p className="text-sm text-destructive">Failed to load tenants.</p>}
          {tenantsQuery.data && tenantsQuery.data.length === 0 && (
            <p className="text-sm text-muted-foreground">No tenants yet.</p>
          )}
          {tenantsQuery.data && tenantsQuery.data.length > 0 && (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Token quota</TableHead>
                  <TableHead>Created</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {tenantsQuery.data.map((tenant) => (
                  <TableRow key={tenant.id}>
                    <TableCell>
                      <Link to="/tenants/$tenantId" params={{ tenantId: tenant.id }} className="font-medium hover:underline">
                        {tenant.name}
                      </Link>
                    </TableCell>
                    <TableCell>{tenant.tokenQuotaPerWindow ?? 'Unlimited'}</TableCell>
                    <TableCell>{new Date(tenant.createdAtUtc).toLocaleString()}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
