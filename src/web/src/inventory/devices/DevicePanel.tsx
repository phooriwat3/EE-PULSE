import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Chip,
  FormControl,
  InputLabel,
  MenuItem,
  Pagination,
  Paper,
  Select,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import { ApiError } from '../../api/client';
import type { CreateDeviceRequest, DeviceResponse, UpdateDeviceRequest } from '../../api/contracts';
import { inventoryApi, type DeviceFilters } from '../../api/inventory';
import { ErrorState, LoadingState, MutationError } from '../feedback';
import { DeviceDialog } from './DeviceDialog';
import { ProbeDialog } from './ProbeDialog';

const initialFilters: DeviceFilters = { page: 1, pageSize: 20 };

export function DevicePanel({ canWrite }: { canWrite: boolean }) {
  const queryClient = useQueryClient();
  const [filters, setFilters] = useState<DeviceFilters>(initialFilters);
  const [draftSearch, setDraftSearch] = useState('');
  const [editing, setEditing] = useState<DeviceResponse | 'new' | null>(null);
  const [probeDevice, setProbeDevice] = useState<DeviceResponse | null>(null);
  const [actionError, setActionError] = useState<unknown>(null);
  const sites = useQuery({ queryKey: ['sites'], queryFn: ({ signal }) => inventoryApi.sites(signal) });
  const devices = useQuery({
    queryKey: ['devices', filters],
    queryFn: ({ signal }) => inventoryApi.devices(filters, signal),
    placeholderData: (previous) => previous,
  });
  const save = useMutation({
    mutationFn: (values: CreateDeviceRequest | UpdateDeviceRequest) =>
      editing === 'new'
        ? inventoryApi.createDevice(values as CreateDeviceRequest)
        : inventoryApi.updateDevice((editing as DeviceResponse).id, values as UpdateDeviceRequest),
    onSuccess: async () => {
      setEditing(null);
      await queryClient.invalidateQueries({ queryKey: ['devices'] });
    },
  });
  const toggle = useMutation({
    mutationFn: (device: DeviceResponse) => inventoryApi.updateDevice(device.id, {
      siteId: device.siteId,
      name: device.name,
      address: device.address,
      hostname: device.hostname,
      deviceType: device.deviceType,
      area: device.area,
      owner: device.owner,
      criticality: device.criticality,
      tags: device.tags,
      enabled: !device.enabled,
      rowVersion: device.rowVersion,
    }),
    onSuccess: async () => {
      setActionError(null);
      await queryClient.invalidateQueries({ queryKey: ['devices'] });
    },
    onError: setActionError,
  });

  if (devices.isPending) return <LoadingState label="Loading Devices" />;
  if (devices.isError) return <ErrorState error={devices.error} retry={() => void devices.refetch()} />;

  const siteMap = new Map(sites.data?.items.map((site) => [site.id, site.name]));
  const totalPages = Math.max(1, Math.ceil(Number(devices.data.totalCount) / Number(devices.data.pageSize)));
  const partialSiteError = sites.isError;
  const reloadDevices = () => {
    setEditing(null);
    save.reset();
    setActionError(null);
    void queryClient.invalidateQueries({ queryKey: ['devices'] });
  };

  return (
    <Stack spacing={3}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ alignItems: { sm: 'center' } }}>
        <Box sx={{ flexGrow: 1 }}>
          <Typography variant="h5" component="h3">Devices</Typography>
          <Typography color="text.secondary">Server-filtered inventory with optimistic concurrency.</Typography>
        </Box>
        {canWrite && <Button variant="contained" onClick={() => setEditing('new')}>Create Device</Button>}
      </Stack>
      {!canWrite && <Alert severity="info">Read-only role. Device and Probe changes require Engineer or Administrator.</Alert>}
      {partialSiteError && (
        <Alert severity="warning" action={<Button color="inherit" onClick={() => void sites.refetch()}>Retry Sites</Button>}>
          Partial data: Devices loaded, but Site names and the Site filter are unavailable.
        </Alert>
      )}
      {actionError !== null && (
        <MutationError
          error={actionError}
          conflictLabel={actionError instanceof ApiError && actionError.status === 409 ? 'Device state conflict' : undefined}
          onReload={reloadDevices}
        />
      )}

      <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Device filters">
        <Stack
          component="form"
          direction={{ xs: 'column', md: 'row' }}
          spacing={2}
          onSubmit={(event) => {
            event.preventDefault();
            setFilters((current) => ({ ...current, search: draftSearch.trim() || undefined, page: 1 }));
          }}
        >
          <TextField
            label="Search Devices"
            value={draftSearch}
            onChange={(event) => setDraftSearch(event.target.value)}
            placeholder="Name, address, or hostname"
            sx={{ minWidth: 240 }}
          />
          <FormControl sx={{ minWidth: 180 }} disabled={!sites.data}>
            <InputLabel id="site-filter-label">Site</InputLabel>
            <Select
              labelId="site-filter-label"
              label="Site"
              value={filters.siteId ?? ''}
              onChange={(event) => setFilters((current) => ({ ...current, siteId: event.target.value || undefined, page: 1 }))}
            >
              <MenuItem value="">All Sites</MenuItem>
              {sites.data?.items.map((site) => <MenuItem key={site.id} value={site.id}>{site.name}</MenuItem>)}
            </Select>
          </FormControl>
          <FormControl sx={{ minWidth: 160 }}>
            <InputLabel id="criticality-filter-label">Criticality</InputLabel>
            <Select
              labelId="criticality-filter-label"
              label="Criticality"
              value={filters.criticality ?? ''}
              onChange={(event) => {
                const value = event.target.value as number | '';
                setFilters((current) => ({
                  ...current,
                  criticality: value === '' ? undefined : Number(value) as 0 | 1 | 2 | 3,
                  page: 1,
                }));
              }}
            >
              <MenuItem value="">All levels</MenuItem>
              <MenuItem value={0}>Low</MenuItem>
              <MenuItem value={1}>Normal</MenuItem>
              <MenuItem value={2}>High</MenuItem>
              <MenuItem value={3}>Critical</MenuItem>
            </Select>
          </FormControl>
          <FormControl sx={{ minWidth: 150 }}>
            <InputLabel id="enabled-filter-label">State</InputLabel>
            <Select
              labelId="enabled-filter-label"
              label="State"
              value={filters.enabled === undefined ? '' : String(filters.enabled)}
              onChange={(event) => setFilters((current) => ({
                ...current,
                enabled: event.target.value === '' ? undefined : event.target.value === 'true',
                page: 1,
              }))}
            >
              <MenuItem value="">All states</MenuItem>
              <MenuItem value="true">Enabled</MenuItem>
              <MenuItem value="false">Disabled</MenuItem>
            </Select>
          </FormControl>
          <Button type="submit" variant="outlined">Search</Button>
          <Button
            onClick={() => { setDraftSearch(''); setFilters(initialFilters); }}
            disabled={filters === initialFilters && draftSearch === ''}
          >
            Clear
          </Button>
        </Stack>
      </Paper>

      {devices.isFetching && devices.data && (
        <Alert severity="info" role="status">Refreshing — showing stale Device data until the server responds.</Alert>
      )}

      {devices.data.items.length === 0 ? (
        <Paper variant="outlined" sx={{ p: 5, textAlign: 'center' }}>
          <Typography variant="h6">No Devices match these filters</Typography>
          <Typography color="text.secondary">Clear filters or create a Device if your role permits it.</Typography>
        </Paper>
      ) : (
        <Stack spacing={1.5} aria-label="Device list">
          {devices.data.items.map((device) => (
            <Paper key={device.id} variant="outlined" sx={{ p: 2 }} component="article">
              <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ alignItems: { md: 'center' } }}>
                <Box sx={{ flexGrow: 1, minWidth: 0 }}>
                  <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
                    <Typography variant="h6" component="h4">{device.name}</Typography>
                    <Chip
                      size="small"
                      label={device.enabled ? 'Enabled' : 'Disabled'}
                      color={device.enabled ? 'success' : 'default'}
                      variant="outlined"
                    />
                    <Chip size="small" label={`Criticality: ${device.criticality}`} variant="outlined" />
                  </Stack>
                  <Typography>{device.address}{device.hostname ? ` · ${device.hostname}` : ''}</Typography>
                  <Typography color="text.secondary">
                    {siteMap.get(device.siteId) ?? `Site ${device.siteId}`} · {device.deviceType}
                    {device.area ? ` · ${device.area}` : ''}
                  </Typography>
                  {device.tags.length > 0 && (
                    <Typography variant="body2" color="text.secondary">Tags: {device.tags.join(', ')}</Typography>
                  )}
                </Box>
                <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1}>
                  <Button variant="outlined" onClick={() => setProbeDevice(device)}>Probe settings</Button>
                  {canWrite && <Button onClick={() => setEditing(device)}>Edit</Button>}
                  {canWrite && (
                    <Button
                      color={device.enabled ? 'warning' : 'primary'}
                      onClick={() => toggle.mutate(device)}
                      disabled={toggle.isPending}
                    >
                      {device.enabled ? 'Soft-disable' : 'Re-enable'}
                    </Button>
                  )}
                </Stack>
              </Stack>
            </Paper>
          ))}
        </Stack>
      )}

      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography color="text.secondary">
          {Number(devices.data.totalCount)} Devices · page {Number(devices.data.page)} of {totalPages}
        </Typography>
        <Pagination
          page={Number(devices.data.page)}
          count={totalPages}
          onChange={(_, page) => setFilters((current) => ({ ...current, page }))}
          color="primary"
        />
      </Stack>

      <DeviceDialog
        device={editing}
        sites={sites.data?.items ?? []}
        open={editing !== null}
        saving={save.isPending}
        error={save.error}
        onClose={() => { save.reset(); setEditing(null); }}
        onReload={reloadDevices}
        onSave={(values) => save.mutate(values)}
      />
      <ProbeDialog device={probeDevice} canWrite={canWrite} onClose={() => setProbeDevice(null)} />
    </Stack>
  );
}
