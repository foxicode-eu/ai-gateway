import { createRootRoute, createRoute, createRouter, Outlet } from '@tanstack/react-router'

import { AppLayout } from '@/components/AppLayout'
import { RequireAuth } from '@/components/RequireAuth'
import { LoginPage } from '@/pages/LoginPage'
import { TenantsListPage } from '@/pages/TenantsListPage'
import { TenantDetailPage } from '@/pages/TenantDetailPage'

const rootRoute = createRootRoute({
  component: () => (
    <AppLayout>
      <Outlet />
    </AppLayout>
  ),
})

const loginRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/login',
  component: LoginPage,
})

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  component: () => (
    <RequireAuth>
      <TenantsListPage />
    </RequireAuth>
  ),
})

const tenantDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/tenants/$tenantId',
  component: TenantDetailRouteComponent,
})

function TenantDetailRouteComponent() {
  const { tenantId } = tenantDetailRoute.useParams()
  return (
    <RequireAuth>
      <TenantDetailPage tenantId={tenantId} />
    </RequireAuth>
  )
}

const routeTree = rootRoute.addChildren([indexRoute, loginRoute, tenantDetailRoute])

export const router = createRouter({ routeTree })

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}
