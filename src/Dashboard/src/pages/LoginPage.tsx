import { useEffect, useState, type FormEvent } from 'react'
import { useNavigate } from '@tanstack/react-router'

import { useAuth } from '@/lib/auth'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'

export function LoginPage() {
  const { login, loginError, isAuthenticated } = useAuth()
  const navigate = useNavigate()
  const [token, setToken] = useState('')
  const [submitting, setSubmitting] = useState(false)

  // Redirect once `isAuthenticated` actually flips true, rather than navigating right after `login()`
  // resolves — the query-cache update from a successful login doesn't synchronously flush a re-render, so a
  // speculative navigate() here would land on "/" while RequireAuth still sees the *previous* (false) value
  // and bounces straight back to /login. Reacting to the real state change avoids that race entirely.
  useEffect(() => {
    if (isAuthenticated) {
      void navigate({ to: '/' })
    }
  }, [isAuthenticated, navigate])

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    try {
      await login(token)
    } catch {
      // surfaced via loginError below
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="flex min-h-svh items-center justify-center p-6">
      <Card className="w-full max-w-sm">
        <CardHeader>
          <CardTitle>AI Gateway admin</CardTitle>
          <CardDescription>
            Paste an admin JWT to sign in. Locally, mint one with{' '}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              dotnet run --project src/DevTools -- mint-token &lt;any-guid&gt;
            </code>
            .
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit} className="flex flex-col gap-4">
            <div className="flex flex-col gap-2">
              <Label htmlFor="token">Token</Label>
              <Input
                id="token"
                value={token}
                onChange={(event) => setToken(event.target.value)}
                placeholder="eyJhbGciOi..."
                autoComplete="off"
                required
              />
            </div>
            {loginError && <p className="text-sm text-destructive">{loginError}</p>}
            <Button type="submit" disabled={submitting || token.length === 0}>
              {submitting ? 'Signing in…' : 'Sign in'}
            </Button>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
