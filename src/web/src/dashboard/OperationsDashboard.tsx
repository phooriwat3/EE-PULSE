import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Button, CircularProgress, Dialog, DialogContent, DialogTitle, FormControl, InputLabel, MenuItem, Select } from '@mui/material';
import { getDashboard } from '../api/dashboard';
import { getIncidentDeviceStatus, getIncidentSnapshot } from '../api/incidentCommands';
import { ApiError, apiRequest, type DevelopmentRole } from '../api/client';
import { IncidentCommands } from './IncidentCommands';
import type { PagedResponseOfSiteResponse } from '../api/generated';
import './operations.css';

function date(value: string | null) {
  if (!value) return 'Not reported';
  const parsed = new Date(value);
  return Number.isNaN(parsed.valueOf()) ? 'Time unavailable' : new Intl.DateTimeFormat('en-GB', { dateStyle: 'medium', timeStyle: 'short' }).format(parsed);
}
function count(value: number) {
  return Number.isSafeInteger(value) && value >= 0 ? value.toLocaleString('en-GB') : 'Count unavailable';
}
function isAccessError(error: unknown) {
  return error instanceof ApiError && (error.status === 401 || error.status === 403);
}
function errorMessage(error: unknown, resource: string) {
  if (error instanceof ApiError) {
    if (error.status === 401) return 'Your session is no longer authorized. Sign out and sign in again.';
    if (error.status === 403) return `Your role cannot access ${resource}.`;
    if (error.status === 404) return 'This incident is no longer available. Close this dialog and refresh the snapshot.';
    if (error.status === 503) return `The ${resource} service is temporarily unavailable. Try again.`;
  }
  return `The ${resource} request could not be completed. Check your connection and try again.`;
}

export function OperationsDashboard({ sessionKey, role }: { sessionKey: string; role?: DevelopmentRole }) {
  const queryClient = useQueryClient();
  const [protectedCommand, setProtectedCommand] = useState(false);
  const [siteId, setSiteId] = useState('');
  const [incidentId, setIncidentId] = useState<string | null>(null);
  const queryOptions = { retry: false, refetchOnWindowFocus: false } as const;
  const sites = useQuery({ ...queryOptions, queryKey: ['dashboard-sites', sessionKey], queryFn: ({ signal }) => apiRequest<PagedResponseOfSiteResponse>('/api/v1/sites?pageSize=200', { signal }) });
  const summary = useQuery({ ...queryOptions, queryKey: ['operations-summary', sessionKey, siteId], queryFn: ({ signal }) => getDashboard(siteId, signal) });
  // Changing the key on close removes the old observer. Consumed AbortSignals
  // cancel pending requests, and a different incident never shows old detail.
  const detail = useQuery({ ...queryOptions, queryKey: ['operations-incident', sessionKey, incidentId], queryFn: ({ signal }) => getIncidentSnapshot(incidentId!, signal), enabled: incidentId !== null });
  const deviceStatus = useQuery({ ...queryOptions, queryKey: ['operations-device-status', sessionKey, detail.data?.incident.deviceId], queryFn: ({ signal }) => getIncidentDeviceStatus(detail.data!.incident.deviceId, signal), enabled: incidentId !== null && !!detail.data && (role === 'Operator' || role === 'Administrator') });
  const data = isAccessError(summary.error) ? undefined : summary.data;
  const incident = isAccessError(detail.error) ? undefined : detail.data?.incident;
  async function refreshIncident() {
    const result = await detail.refetch();
    await deviceStatus.refetch();
    // Unavailable probe state disables resolution, not unrelated comment/acknowledgement actions.
    if (result.isError) throw new Error('Refresh failed.');
  }
  async function commandSucceeded() {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['operations-incident', sessionKey, incidentId] }),
      queryClient.invalidateQueries({ queryKey: ['operations-device-status', sessionKey] }),
      queryClient.invalidateQueries({ queryKey: ['operations-summary', sessionKey] }),
    ]);
  }

  return <main className="operations">
    <header className="operations-heading">
      <div><p className="eyebrow">EE PULSE / OPERATIONS</p><h1>Operations overview</h1><p className="operations-subtitle">Equipment availability and incidents across monitored sites.</p></div>
      <div className="operations-tools">
        <FormControl size="small" sx={{ minWidth: 190 }}><InputLabel id="operations-site">Site</InputLabel>
          <Select labelId="operations-site" label="Site" value={siteId} onChange={event => { setSiteId(event.target.value); setIncidentId(null); }}>
            <MenuItem value="">All sites</MenuItem>
            {!isAccessError(sites.error) && sites.data?.items.map(site => <MenuItem key={site.id} value={site.id}>{site.name}</MenuItem>)}
          </Select>
        </FormControl>
        <Button variant="outlined" disabled={summary.isFetching} onClick={() => void summary.refetch()}>Refresh</Button>
      </div>
    </header>
    <div className="operations-sync" role="status"><span className="sync-dot" aria-hidden="true" />
      {summary.isFetching ? 'Updating snapshot...' : summary.isError ? 'Latest snapshot unavailable' : summary.dataUpdatedAt ? `Snapshot received ${new Date(summary.dataUpdatedAt).toLocaleTimeString('en-GB')} · Manual refresh` : 'Waiting for snapshot'}
      <span>Times shown in your browser timezone</span>
    </div>
    {sites.isError && <Alert severity="warning" action={<Button disabled={sites.isFetching} onClick={() => void sites.refetch()}>Retry sites</Button>}>{errorMessage(sites.error, 'site choices')} The current selection is retained.</Alert>}
    {sites.data && Number(sites.data.totalCount) > sites.data.items.length && <p className="panel-note">Site choices show the first 200 sites. Only the first 200 sites are available in this selector. Browsing additional sites is deferred.</p>}
    {summary.isError && <Alert severity="error" action={!isAccessError(summary.error) && <Button disabled={summary.isFetching} onClick={() => void summary.refetch()}>Retry snapshot</Button>}>
      {errorMessage(summary.error, 'operations snapshot')}{data && ' Previously loaded data is shown below and may be stale.'}
    </Alert>}
    {summary.isFetching && data && <p className="snapshot-notice">Showing the previous snapshot while refresh is in progress.</p>}
    {summary.isPending && <div className="operations-loading" role="status"><CircularProgress size={24} aria-label="Loading operations snapshot" /><span>Loading operations snapshot</span></div>}
    {data && <>
      <section className="status-grid" aria-label="Probe status counts">
        {data.statusCounts.map(item => <article className={`status-card status-${item.status.toLowerCase()}`} key={item.status}><div><span className="status-dot" aria-hidden="true" />{item.status}</div><strong>{count(item.count)}</strong><small>Monitored probes</small></article>)}
      </section>
      <section className="operations-panel" aria-labelledby="open-incidents-heading">
        <div className="panel-heading"><div><p className="eyebrow">INCIDENTS</p><h2 id="open-incidents-heading">Open incidents <span className="count-label">{data.openIncidents.length}</span></h2></div><span className="panel-note">Up to 20 active incidents</span></div>
        {data.openIncidents.length === 0 ? <div className="operations-empty"><h3>No open incidents in this snapshot</h3><p>This is a point-in-time result. Refresh to request the latest snapshot.</p></div> : <div className="operations-table-wrap" tabIndex={0} role="region" aria-label="Open incidents table, scroll horizontally on smaller screens">
          <table className="operations-table"><caption className="sr-only">Active incidents in the selected site snapshot</caption>
            <thead><tr><th scope="col">Equipment</th><th scope="col">Site</th><th scope="col">Status</th><th scope="col">Criticality</th><th scope="col">Opened</th><th scope="col"><span className="sr-only">Details</span></th></tr></thead>
            <tbody>{data.openIncidents.map(item => <tr key={item.incidentId}><td><strong>{item.deviceName}</strong><small>{count(item.occurrenceCount)} occurrences</small></td><td>{item.siteName}</td><td><span className={`state-badge ${item.status.toLowerCase()}`}>{item.status}</span></td><td>{item.criticality}</td><td>{date(item.openedAt)}</td><td><button className="detail-button" onClick={() => setIncidentId(item.incidentId)} aria-label={`View incident for ${item.deviceName}`}>View incident <span aria-hidden="true">→</span></button></td></tr>)}</tbody>
          </table>
        </div>}
      </section>
      <div className="operations-lower">
        <section className="operations-panel" aria-labelledby="recently-down-heading">
          <div className="panel-heading"><div><p className="eyebrow">EQUIPMENT</p><h2 id="recently-down-heading">Recently down</h2></div><span className="panel-note">Up to 20 probes</span></div>
          {data.recentlyDown.length === 0 ? <p className="list-empty">No recently down probes in this snapshot.</p> : <ul className="operations-list">{data.recentlyDown.map(item => <li key={item.probeId}><div><strong>{item.deviceName}</strong><small>{item.siteName} · {item.area ?? 'Area not assigned'}</small><small>Down since {date(item.sinceAt)}</small></div>{item.openIncidentId && <button className="detail-button" aria-label={`View down incident for ${item.deviceName}`} onClick={() => setIncidentId(item.openIncidentId)}>View incident</button>}</li>)}</ul>}
        </section>
        <section className="operations-panel" aria-labelledby="offline-agents-heading">
          <div className="panel-heading"><div><p className="eyebrow">MONITORING COVERAGE</p><h2 id="offline-agents-heading">Offline agents</h2></div><span className="panel-note">Up to 20 agents</span></div>
          {data.offlineAgents.length === 0 ? <p className="list-empty">No offline agents in this snapshot.</p> : <ul className="operations-list">{data.offlineAgents.map(agent => <li key={agent.agentId}><div><strong>{agent.agentName}</strong><small>Last heartbeat: {date(agent.lastHeartbeatAt)}</small><small>Self-health: {agent.selfHealth}</small></div><span className="state-badge offline">Offline</span></li>)}</ul>}
        </section>
      </div>
    </>}
    <Dialog open={incidentId !== null} onClose={() => { if (!protectedCommand) setIncidentId(null); }} aria-labelledby="incident-dialog-heading" fullWidth maxWidth="sm">
      <DialogTitle id="incident-dialog-heading"><div className="incident-dialog-title">Incident details<Button disabled={protectedCommand} onClick={() => setIncidentId(null)}>Close</Button></div></DialogTitle>
      <DialogContent>
        {detail.isPending && incidentId && <p role="status">Loading incident...</p>}
        {detail.isError && <Alert severity="error" action={!isAccessError(detail.error) && <Button disabled={detail.isFetching} onClick={() => void detail.refetch()}>Retry detail</Button>}>{errorMessage(detail.error, 'incident detail')}{incident && ' Previously loaded detail may be stale.'}</Alert>}
        {detail.isFetching && incident && <p role="status">Showing the previous detail while updating.</p>}
        {incident && <><h2>{incident.deviceName}</h2><span className={`state-badge ${incident.status.toLowerCase()}`}>{incident.status}</span>
          <dl className="incident-facts"><dt>Site</dt><dd>{incident.siteName}</dd><dt>Opened</dt><dd>{date(incident.openedAt)}</dd><dt>Acknowledged</dt><dd>{date(incident.acknowledgedAt)}</dd><dt>Resolved</dt><dd>{date(incident.resolvedAt)}</dd><dt>Occurrences</dt><dd>{count(incident.occurrenceCount)}</dd><dt>Rule</dt><dd>{incident.ruleKey}</dd><dt>Acknowledgement</dt><dd>{incident.acknowledgementComment ?? 'Not recorded'}</dd><dt>Resolution note</dt><dd>{incident.resolutionNote ?? 'Not recorded'}</dd></dl>
          <IncidentCommands key={`${sessionKey}:${incident.id}`} snapshot={detail.data!} role={role} ready={!detail.isFetching && !detail.isError} probeStatus={!deviceStatus.isError && !deviceStatus.isFetching ? deviceStatus.data?.probes.find(probe => probe.probeId === incident.probeId)?.underlyingStatus : undefined} onRefresh={refreshIncident} onSuccess={commandSucceeded} onProtectedChange={setProtectedCommand} />
        </>}
      </DialogContent>
    </Dialog>
  </main>;
}
