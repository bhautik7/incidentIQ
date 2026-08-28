import { lazy } from 'react'
import { createBrowserRouter } from 'react-router'

import { AppShell } from '../components/layout/AppShell'
import { RouteError } from '../components/layout/RouteError'

/**
 * Every page is a separate chunk.
 *
 * The log explorer and the analytics charts pull in heavy dependencies that
 * someone looking at the incident list should never pay to download.
 */
const Overview = lazy(() => import('../routes/Overview'))
const Incidents = lazy(() => import('../routes/Incidents'))
const IncidentDetail = lazy(() => import('../routes/IncidentDetail'))
const Logs = lazy(() => import('../routes/Logs'))
const Diagnose = lazy(() => import('../routes/Diagnose'))
const Services = lazy(() => import('../routes/Services'))
const ServiceDetail = lazy(() => import('../routes/ServiceDetail'))
const Deployments = lazy(() => import('../routes/Deployments'))
const DeploymentDetail = lazy(() => import('../routes/DeploymentDetail'))
const AiInvestigations = lazy(() => import('../routes/AiInvestigations'))
const Analytics = lazy(() => import('../routes/Analytics'))
const AlertRules = lazy(() => import('../routes/AlertRules'))
const Team = lazy(() => import('../routes/Team'))
const Settings = lazy(() => import('../routes/Settings'))

export const router = createBrowserRouter([
  {
    path: '/',
    element: <AppShell />,
    errorElement: <RouteError />,
    children: [
      { index: true, element: <Overview /> },
      { path: 'incidents', element: <Incidents /> },
      { path: 'incidents/:incidentId', element: <IncidentDetail /> },
      { path: 'logs', element: <Logs /> },
      { path: 'diagnose', element: <Diagnose /> },
      { path: 'services', element: <Services /> },
      { path: 'services/:serviceKey', element: <ServiceDetail /> },
      { path: 'deployments', element: <Deployments /> },
      { path: 'deployments/:deploymentId', element: <DeploymentDetail /> },
      { path: 'ai-investigations', element: <AiInvestigations /> },
      { path: 'analytics', element: <Analytics /> },
      { path: 'alert-rules', element: <AlertRules /> },
      { path: 'team', element: <Team /> },
      { path: 'settings', element: <Settings /> },
      { path: '*', element: <RouteError notFound /> },
    ],
  },
])
