import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  FormControlLabel,
  InputLabel,
  MenuItem,
  Paper,
  Select,
  Stack,
  Switch,
  TextField,
  Typography,
} from '@mui/material';
import type { CreateProbeRequest, DeviceResponse, ProbeResponse, UpdateProbeRequest } from '../../api/contracts';
import { inventoryApi } from '../../api/inventory';
import { ErrorState, LoadingState, MutationError } from '../feedback';

export function ProbeDialog({ device, canWrite, onClose }: {
  device: DeviceResponse | null;
  canWrite: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<ProbeResponse | 'new' | null>(null);
  const probes = useQuery({
    queryKey: ['probes', device?.id],
    queryFn: ({ signal }) => inventoryApi.probes(device!.id, signal),
    enabled: Boolean(device),
  });
  const groups = useQuery({
    queryKey: ['agent-groups'],
    queryFn: ({ signal }) => inventoryApi.agentGroups(signal),
    enabled: Boolean(device),
  });
  const save = useMutation({
    mutationFn: (request: CreateProbeRequest | UpdateProbeRequest) =>
      editing === 'new'
        ? inventoryApi.createProbe(request as CreateProbeRequest)
        : inventoryApi.updateProbe((editing as ProbeResponse).id, request as UpdateProbeRequest),
    onSuccess: async () => {
      setEditing(null);
      await queryClient.invalidateQueries({ queryKey: ['probes', device?.id] });
    },
  });
  const reloadProbes = () => {
    setEditing(null);
    save.reset();
    void queryClient.invalidateQueries({ queryKey: ['probes', device?.id] });
  };

  const close = () => {
    setEditing(null);
    save.reset();
    onClose();
  };

  return (
    <Dialog open={Boolean(device)} onClose={close} fullWidth maxWidth="md">
      <DialogTitle>Probe settings — {device?.name}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {!canWrite && <Alert severity="info">Probe settings are read-only for this role.</Alert>}
          {(probes.isPending || groups.isPending) && <LoadingState label="Loading Probe configuration" />}
          {probes.isError && <ErrorState error={probes.error} retry={() => void probes.refetch()} />}
          {groups.isError && (
            <Alert severity="warning" action={<Button color="inherit" onClick={() => void groups.refetch()}>Retry</Button>}>
              Partial data: Probes may be visible, but Agent Group names and editing are unavailable.
            </Alert>
          )}
          {probes.data?.items.length === 0 && (
            <Paper variant="outlined" sx={{ p: 3, textAlign: 'center' }}>
              <Typography variant="h6">No Probes configured</Typography>
              <Typography color="text.secondary">Add an ICMP Probe to monitor this Device.</Typography>
            </Paper>
          )}
          {probes.data?.items.map((probe) => (
            <Paper key={probe.id} variant="outlined" sx={{ p: 2 }}>
              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ alignItems: { sm: 'center' } }}>
                <Box sx={{ flexGrow: 1 }}>
                  <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                    <Typography variant="h6" component="h4">{probe.type}</Typography>
                    <Chip label={probe.enabled ? 'Enabled' : 'Disabled'} size="small" variant="outlined" />
                  </Stack>
                  <Typography color="text.secondary">
                    Every {String(probe.intervalSeconds)}s · timeout {String(probe.timeoutMilliseconds)}ms · {String(probe.attemptCount)} attempts
                  </Typography>
                  <Typography variant="body2">
                    Warning RTT {probe.warningRttMilliseconds === null ? 'not set' : `${String(probe.warningRttMilliseconds)}ms`};{' '}
                    Critical RTT {probe.criticalRttMilliseconds === null ? 'not set' : `${String(probe.criticalRttMilliseconds)}ms`};{' '}
                    failure/recovery {String(probe.failureThreshold)}/{String(probe.recoveryThreshold)}
                  </Typography>
                  <Typography variant="body2" color="text.secondary">
                    Agent Group: {groups.data?.items.find((group) => group.id === probe.agentGroupId)?.name ?? probe.agentGroupId}
                  </Typography>
                </Box>
                {canWrite && groups.data && <Button onClick={() => setEditing(probe)}>Edit Probe</Button>}
              </Stack>
            </Paper>
          ))}
          {canWrite && groups.data && groups.data.items.length > 0 && !editing && (
            <Button variant="outlined" onClick={() => setEditing('new')}>Add ICMP Probe</Button>
          )}
          {canWrite && groups.data?.items.length === 0 && (
            <Alert severity="warning">No enabled Agent Group is available. Create one through the inventory API before adding a Probe.</Alert>
          )}
          {editing && device && groups.data && (
            <ProbeForm
              key={editing === 'new' ? 'new' : editing.id}
              editing={editing}
              device={device}
              groups={groups.data.items}
              saving={save.isPending}
              error={save.error}
              onCancel={() => { save.reset(); setEditing(null); }}
              onReload={reloadProbes}
              onSave={(request) => save.mutate(request)}
            />
          )}
        </Stack>
      </DialogContent>
      <DialogActions><Button onClick={close}>Close</Button></DialogActions>
    </Dialog>
  );
}

function ProbeForm({ editing, device, groups, saving, error, onCancel, onReload, onSave }: {
  editing: ProbeResponse | 'new';
  device: DeviceResponse;
  groups: Awaited<ReturnType<typeof inventoryApi.agentGroups>>['items'];
  saving: boolean;
  error: unknown;
  onCancel: () => void;
  onReload: () => void;
  onSave: (request: CreateProbeRequest | UpdateProbeRequest) => void;
}) {
  const old = editing === 'new' ? null : editing;
  return (
    <Paper
      component="form"
      variant="outlined"
      sx={{ p: 2 }}
      onSubmit={(event) => {
        event.preventDefault();
        const data = new FormData(event.currentTarget);
        const number = (name: string) => Number(data.get(name));
        const nullableNumber = (name: string) => {
          const value = String(data.get(name) ?? '').trim();
          return value === '' ? null : Number(value);
        };
        const common = {
          agentGroupId: String(data.get('agentGroupId') ?? ''),
          intervalSeconds: number('intervalSeconds'),
          timeoutMilliseconds: number('timeoutMilliseconds'),
          attemptCount: number('attemptCount'),
          warningRttMilliseconds: nullableNumber('warningRttMilliseconds'),
          criticalRttMilliseconds: nullableNumber('criticalRttMilliseconds'),
          failureThreshold: number('failureThreshold'),
          recoveryThreshold: number('recoveryThreshold'),
        };
        onSave(old
          ? { ...common, enabled: data.get('enabled') === 'on', rowVersion: old.rowVersion }
          : { ...common, deviceId: device.id });
      }}
    >
      <Stack spacing={2}>
        <Typography variant="h6" component="h4">{old ? 'Edit ICMP Probe' : 'Add ICMP Probe'}</Typography>
        <MutationError error={error} conflictLabel="Probe update conflict" onReload={onReload} />
        <FormControl fullWidth required>
          <InputLabel id="probe-agent-group-label">Agent Group</InputLabel>
          <Select
            name="agentGroupId"
            labelId="probe-agent-group-label"
            label="Agent Group"
            defaultValue={old?.agentGroupId ?? groups[0]?.id ?? ''}
          >
            {groups.map((group) => <MenuItem key={group.id} value={group.id}>{group.name}</MenuItem>)}
          </Select>
        </FormControl>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
          <TextField name="intervalSeconds" label="Interval (seconds)" type="number" defaultValue={old ? String(old.intervalSeconds) : '30'} required fullWidth />
          <TextField name="timeoutMilliseconds" label="Timeout (ms)" type="number" defaultValue={old ? String(old.timeoutMilliseconds) : '2000'} required fullWidth />
          <TextField name="attemptCount" label="Attempts" type="number" defaultValue={old ? String(old.attemptCount) : '3'} required fullWidth />
        </Stack>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
          <TextField name="warningRttMilliseconds" label="Warning RTT (ms)" type="number" defaultValue={old?.warningRttMilliseconds === null ? '' : String(old?.warningRttMilliseconds ?? '')} fullWidth />
          <TextField name="criticalRttMilliseconds" label="Critical RTT (ms)" type="number" defaultValue={old?.criticalRttMilliseconds === null ? '' : String(old?.criticalRttMilliseconds ?? '')} fullWidth />
        </Stack>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
          <TextField name="failureThreshold" label="Failure threshold" type="number" defaultValue={old ? String(old.failureThreshold) : '3'} required fullWidth />
          <TextField name="recoveryThreshold" label="Recovery threshold" type="number" defaultValue={old ? String(old.recoveryThreshold) : '2'} required fullWidth />
        </Stack>
        {old && <FormControlLabel control={<Switch name="enabled" defaultChecked={old.enabled} />} label="Enabled" />}
        <Stack direction="row" spacing={1} sx={{ justifyContent: 'flex-end' }}>
          <Button onClick={onCancel} disabled={saving}>Cancel</Button>
          <Button type="submit" variant="contained" disabled={saving}>{saving ? 'Saving…' : 'Save Probe'}</Button>
        </Stack>
      </Stack>
    </Paper>
  );
}
