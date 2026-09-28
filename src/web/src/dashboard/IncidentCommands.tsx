import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, FormControl, InputLabel, MenuItem, Select, TextField } from '@mui/material';
import { ApiError, type DevelopmentRole } from '../api/client';
import { createCommandIntent, incidentProblemCode, incidentTextError, sendIncidentCommand, type CommandIntent, type CommandKind, type IncidentSnapshot } from '../api/incidentCommands';
import type { DashboardProbeStatus } from '../api/generated';

type Phase = 'editing' | 'pending' | 'success' | 'validation' | 'access' | 'stale' | 'conflict' | 'reuse' | 'unavailable' | 'uncertain';
function failure(error: unknown, cancelled: boolean): { phase: Phase; message: string } {
  if (cancelled) return { phase: 'uncertain', message: 'Stopped waiting. The request may have completed on the server. Retry the saved request to confirm its outcome.' };
  if (error instanceof ApiError) {
    if (error.status === 401 || error.status === 403) return { phase: 'access', message: 'You are not authorized to perform this action. Sign out and sign in with an authorized account.' };
    if (error.status === 412) return { phase: 'stale', message: 'This incident changed. Refresh the incident, review its current state, and submit again.' };
    if (error.status === 409) {
      const code = incidentProblemCode(error);
      if (code === 'idempotency-key-reuse-conflict') return { phase: 'reuse', message: 'This saved request conflicts with an earlier request. Refresh and review before explicitly submitting a new request.' };
      return { phase: 'conflict', message: code === 'incident-manual-resolution-state-conflict' ? 'Resolution is not allowed while the underlying probe is Down or Recovering. Refresh the incident and probe status.' : 'This action is not allowed in the current incident state. Refresh and review the incident.' };
    }
    if (error.status === 400 || error.status === 428) return { phase: 'validation', message: 'The request was not accepted. Review the comment or note, then try again. Refresh the incident if the problem continues.' };
    if (error.status === 404) return { phase: 'access', message: 'This incident is no longer available. Close the dialog and refresh the snapshot.' };
    if (error.status === 503) return { phase: 'unavailable', message: 'The service is temporarily unavailable. The outcome is not confirmed. Retry the saved request; do not submit another copy.' };
  }
  return { phase: 'uncertain', message: 'The outcome is not confirmed. The request may have completed. Retry the saved request; do not submit another copy.' };
}
export function IncidentCommands({ snapshot, role, ready, probeStatus, onRefresh, onSuccess, onProtectedChange }: {
  snapshot: IncidentSnapshot; role?: DevelopmentRole; ready: boolean; probeStatus?: DashboardProbeStatus;
  onRefresh: () => Promise<void>; onSuccess: () => Promise<void>; onProtectedChange: (value: boolean) => void;
}) {
  const [kind, setKind] = useState<CommandKind>('comments');
  const [text, setText] = useState('');
  const [phase, setPhase] = useState<Phase>('editing');
  const [message, setMessage] = useState('');
  const [intent, setIntent] = useState<CommandIntent | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const refreshGuard = useRef(false);
  const [confirmation, setConfirmation] = useState<'resolve' | 'discard' | null>(null);
  const inFlight = useRef<AbortController | null>(null);
  const mounted = useRef(true);
  const feedback = useRef<HTMLDivElement>(null);
  const confirmationBack = useRef<HTMLButtonElement>(null);
  const uncertain = phase === 'uncertain' || phase === 'unavailable';
  const protectedRequest = phase === 'pending' || uncertain;
  const allowed = role === 'Operator' || role === 'Administrator';
  const blockedResolution = probeStatus === undefined || probeStatus === 'Down' || probeStatus === 'Recovering';
  const stateAllowed = kind === 'comments' || (kind === 'acknowledge' ? snapshot.incident.status === 'Open' : snapshot.incident.status !== 'Resolved' && !blockedResolution);
  useEffect(() => { onProtectedChange(protectedRequest); return () => onProtectedChange(false); }, [protectedRequest, onProtectedChange]);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; inFlight.current?.abort(); }; }, []);
  useEffect(() => { if (message) feedback.current?.focus(); }, [message]);
  async function execute(saved: CommandIntent) {
    if (inFlight.current) return; // Synchronous guard, including two clicks before React commits.
    const controller = new AbortController();
    inFlight.current = controller;
    setIntent(saved); setPhase('pending'); setMessage('Sending request. Do not submit another copy.');
    try {
      await sendIncidentCommand(saved, controller.signal);
      if (!mounted.current) return;
      setRefreshing(true); refreshGuard.current = true;
      setPhase('success'); setIntent(null); setText(''); setMessage('Request confirmed. Updating the incident and dashboard.');
      try {
        await onSuccess();
        if (mounted.current) setMessage('Request confirmed. Check the latest incident and dashboard snapshots.');
      } catch { if (mounted.current) setMessage('Request confirmed, but the latest snapshot could not be loaded. Refresh before another action.'); }
    } catch (error) {
      if (!mounted.current) return;
      const classified = failure(error, controller.signal.aborted);
      setPhase(classified.phase); setMessage(classified.message);
      if (classified.phase === 'validation') setIntent(null);
    } finally {
      if (inFlight.current === controller) inFlight.current = null;
      refreshGuard.current = false;
      if (mounted.current) setRefreshing(false);
    }
  }
  function submit() {
    if (inFlight.current || refreshGuard.current || protectedRequest || !allowed || !ready || !snapshot.etag || !stateAllowed) return;
    const error = incidentTextError(text);
    if (error) { setPhase('validation'); setMessage(error); return; }
    if (kind === 'resolve') { setConfirmation('resolve'); return; }
    void execute(createCommandIntent(kind, snapshot.incident.id, snapshot.etag, text));
  }
  async function refresh() {
    if (inFlight.current || refreshGuard.current) return;
    refreshGuard.current = true; setRefreshing(true);
    try {
      await onRefresh();
      if (mounted.current) { setIntent(null); setPhase('editing'); setMessage('Incident refreshed. Review the state and submit a new request when ready.'); }
    } catch { if (mounted.current) setMessage('The incident could not be refreshed. Your text is retained.'); }
    finally { refreshGuard.current = false; if (mounted.current) setRefreshing(false); }
  }
  if (!allowed) return <p className="panel-note">Read-only access. Incident actions require Operator or Administrator access.</p>;
  return <section className="incident-commands" aria-labelledby="incident-actions-heading">
    <h3 id="incident-actions-heading">Incident actions</h3>
    <p className="panel-note">Changes are applied only after the server confirms success. Comments are permanent.</p>
    {!snapshot.etag && <Alert severity="warning">The incident version is unavailable. Refresh before submitting an action.</Alert>}
    <FormControl fullWidth size="small"><InputLabel id="incident-action-label">Action</InputLabel><Select labelId="incident-action-label" label="Action" value={kind} disabled={protectedRequest || refreshing || phase === 'access'} onChange={event => { setKind(event.target.value as CommandKind); setIntent(null); if (!['stale', 'conflict', 'reuse'].includes(phase)) { setPhase('editing'); setMessage(''); } }}>
      <MenuItem value="comments">Add comment</MenuItem><MenuItem value="acknowledge" disabled={snapshot.incident.status !== 'Open'}>Acknowledge</MenuItem><MenuItem value="resolve" disabled={snapshot.incident.status === 'Resolved'}>Resolve</MenuItem>
    </Select></FormControl>
    {kind === 'resolve' && <p className="panel-note">Resolution requires an active incident and an underlying probe that is neither Down nor Recovering. {probeStatus ? `Current probe: ${probeStatus}.` : 'Probe status is not available.'}</p>}
    <TextField fullWidth multiline minRows={3} label={kind === 'resolve' ? 'Resolution note' : kind === 'acknowledge' ? 'Acknowledgement comment' : 'Comment'} value={text} required disabled={protectedRequest || refreshing || phase === 'access'} error={phase === 'validation'} helperText={`${text.length.toLocaleString('en-GB')} / 2,000 character units; emoji count as two. Padding counts before trimming.`} onChange={event => { setText(event.target.value); setIntent(null); if (!['stale', 'conflict', 'reuse'].includes(phase)) { setPhase('editing'); setMessage(''); } }} />
    {message && <div ref={feedback} tabIndex={-1}><Alert severity={phase === 'success' ? 'success' : phase === 'pending' ? 'info' : 'warning'} role={phase === 'pending' || phase === 'success' ? 'status' : 'alert'}>{message}</Alert></div>}
    <div className="command-buttons">
      {!uncertain && <Button variant="contained" disabled={refreshing || !ready || !snapshot.etag || !stateAllowed || ['pending', 'access', 'stale', 'conflict', 'reuse'].includes(phase)} onClick={submit}>{phase === 'pending' ? 'Sending...' : kind === 'comments' ? 'Submit comment' : kind === 'acknowledge' ? 'Acknowledge incident' : 'Review resolution'}</Button>}
      {phase === 'pending' && <Button onClick={() => inFlight.current?.abort()}>Stop waiting</Button>}
      {uncertain && intent && <><Button variant="contained" onClick={() => void execute(intent)}>Retry saved request</Button><Button onClick={() => setConfirmation('discard')}>Discard saved request</Button></>}
      {!protectedRequest && phase !== 'access' && <Button disabled={!ready || refreshing} onClick={() => void refresh()}>Refresh incident</Button>}
    </div>
    {protectedRequest && <p className="panel-note">Keep this dialog open to retain the saved request. Stopping or closing the browser does not prove the server rolled back.</p>}
    <Dialog open={confirmation !== null} onClose={() => setConfirmation(null)} aria-labelledby="command-confirmation-heading" slotProps={{ transition: { onEntered: () => confirmationBack.current?.focus() } }}>
      <DialogTitle id="command-confirmation-heading">{confirmation === 'resolve' ? 'Confirm incident resolution' : 'Discard the saved request?'}</DialogTitle>
      <DialogContent>{confirmation === 'resolve' ? <><p>This manually resolves the incident. Review the note before confirming.</p><p className="command-note">{text}</p></> : <p>The earlier request may already have completed. Discarding removes your exact retry information. Review the refreshed incident before explicitly creating a different request.</p>}</DialogContent>
      <DialogActions><Button ref={confirmationBack} autoFocus onClick={() => setConfirmation(null)}>Back</Button><Button variant="contained" onClick={() => {
        const chosen = confirmation; setConfirmation(null);
        if (chosen === 'resolve' && ready && snapshot.etag && stateAllowed && !inFlight.current && !refreshGuard.current) void execute(createCommandIntent(kind, snapshot.incident.id, snapshot.etag, text));
        if (chosen === 'discard') { setIntent(null); setPhase('stale'); setMessage('Saved request discarded. The earlier outcome may still be committed. Refresh and review before submitting again.'); }
      }}>{confirmation === 'resolve' ? 'Confirm resolution' : 'Discard and review'}</Button></DialogActions>
    </Dialog>
  </section>;
}
