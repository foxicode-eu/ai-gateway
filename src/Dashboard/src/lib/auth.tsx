import { createContext, use, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, ApiError } from '@/lib/api'

interface AuthContextValue {
  isAuthenticated: boolean
  isLoading: boolean
  login: (token: string) => Promise<void>
  loginError: string | null
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()

  const sessionQuery = useQuery({
    queryKey: ['session'],
    queryFn: async () => {
      try {
        return await api.session()
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) {
          return { authenticated: false }
        }
        throw error
      }
    },
    retry: false,
    staleTime: 60_000,
  })

  const loginMutation = useMutation({
    mutationFn: api.login,
    onSuccess: () => queryClient.setQueryData(['session'], { authenticated: true }),
  })

  const logoutMutation = useMutation({
    mutationFn: api.logout,
    onSuccess: () => queryClient.setQueryData(['session'], { authenticated: false }),
  })

  const value: AuthContextValue = {
    isAuthenticated: sessionQuery.data?.authenticated ?? false,
    isLoading: sessionQuery.isLoading,
    login: async (token) => {
      await loginMutation.mutateAsync(token)
    },
    loginError: loginMutation.error instanceof ApiError ? loginMutation.error.message : null,
    logout: async () => {
      await logoutMutation.mutateAsync()
    },
  }

  return <AuthContext value={value}>{children}</AuthContext>
}

export function useAuth() {
  const context = use(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used within an AuthProvider')
  }
  return context
}
