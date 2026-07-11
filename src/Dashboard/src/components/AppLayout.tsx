import type { ReactNode } from 'react'
import { Link, useNavigate } from '@tanstack/react-router'

import { useAuth } from '@/lib/auth'
import { Button } from '@/components/ui/button'

export function AppLayout({ children }: { children: ReactNode }) {
  const { isAuthenticated, logout } = useAuth()
  const navigate = useNavigate()

  return (
    <div className="min-h-svh bg-background">
      <header className="border-b">
        <div className="mx-auto flex max-w-5xl items-center justify-between px-6 py-4">
          <Link to="/" className="font-semibold">
            AI Gateway
          </Link>
          {isAuthenticated && (
            <Button
              variant="ghost"
              size="sm"
              onClick={async () => {
                await logout()
                await navigate({ to: '/login' })
              }}
            >
              Sign out
            </Button>
          )}
        </div>
      </header>
      <main className="mx-auto max-w-5xl px-6 py-8">{children}</main>
    </div>
  )
}
