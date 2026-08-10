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
  FormControlLabel,
  Paper,
  Stack,
  Switch,
  TextField,
  Typography,
} from '@mui/material';
import { inventoryApi } from '../../api/inventory';
import type { CreateSiteRequest, SiteResponse, UpdateSiteRequest } from '../../api/contracts';
import { ErrorState, LoadingState, MutationError } from '../feedback';

export function SitePanel({ canManage }: { canManage: boolean }) {
  const queryClient = useQueryClient();
  const sites = useQuery({ queryKey: ['sites'], queryFn: ({ signal }) => inventoryApi.sites(signal) });
  const [editing, setEditing] = useState<SiteResponse | 'new' | null>(null);
  const mutation = useMutation({
    mutationFn: (values: CreateSiteRequest | UpdateSiteRequest) =>
      editing === 'new'
        ? inventoryApi.createSite(values as CreateSiteRequest)
        : inventoryApi.updateSite((editing as SiteResponse).id, values as UpdateSiteRequest),
    onSuccess: async () => {
      setEditing(null);
      await queryClient.invalidateQueries({ queryKey: ['sites'] });
    },
  });
  const reloadSites = () => {
    setEditing(null);
    mutation.reset();
    void queryClient.invalidateQueries({ queryKey: ['sites'] });
  };

  if (sites.isPending) return <LoadingState label="Loading Sites" />;
  if (sites.isError) return <ErrorState error={sites.error} retry={() => void sites.refetch()} />;

  return (
    <Stack spacing={2}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ alignItems: { sm: 'center' } }}>
        <Box sx={{ flexGrow: 1 }}>
          <Typography variant="h5" component="h3">Sites</Typography>
          <Typography color="text.secondary">Site code, display name, timezone, and lifecycle state.</Typography>
        </Box>
        {canManage && <Button variant="contained" onClick={() => setEditing('new')}>Create Site</Button>}
      </Stack>
      {!canManage && (
        <Alert severity="info">Read-only role. Site management requires the Administrator role.</Alert>
      )}
      {sites.isFetching && <Alert severity="info" role="status">Refreshing — showing the last successful Site list.</Alert>}
      {sites.data.items.length === 0 ? (
        <Paper variant="outlined" sx={{ p: 4, textAlign: 'center' }}>
          <Typography variant="h6">No Sites found</Typography>
          <Typography color="text.secondary">An Administrator can create the first Site.</Typography>
        </Paper>
      ) : (
        <Stack spacing={1}>
          {sites.data.items.map((site) => (
            <Paper key={site.id} variant="outlined" sx={{ p: 2 }}>
              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ alignItems: { sm: 'center' } }}>
                <Box sx={{ flexGrow: 1 }}>
                  <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
                    <Typography variant="h6" component="h4">{site.name}</Typography>
                    <Chip
                      size="small"
                      label={site.enabled ? 'Enabled' : 'Disabled'}
                      color={site.enabled ? 'success' : 'default'}
                      variant="outlined"
                    />
                  </Stack>
                  <Typography color="text.secondary">Code {site.code} · Timezone {site.timezone}</Typography>
                </Box>
                {canManage && <Button onClick={() => setEditing(site)}>Edit Site</Button>}
              </Stack>
            </Paper>
          ))}
        </Stack>
      )}
      <SiteDialog
        site={editing}
        open={editing !== null}
        saving={mutation.isPending}
        error={mutation.error}
        onClose={() => { mutation.reset(); setEditing(null); }}
        onReload={reloadSites}
        onSave={(values) => mutation.mutate(values)}
      />
    </Stack>
  );
}

function SiteDialog({ site, open, saving, error, onClose, onReload, onSave }: {
  site: SiteResponse | 'new' | null;
  open: boolean;
  saving: boolean;
  error: unknown;
  onClose: () => void;
  onReload: () => void;
  onSave: (request: CreateSiteRequest | UpdateSiteRequest) => void;
}) {
  const key = site === 'new' ? 'new' : (site?.id ?? 'closed');
  return (
    <Dialog key={key} open={open} onClose={saving ? undefined : onClose} fullWidth maxWidth="sm">
      <Box
        component="form"
        onSubmit={(event) => {
          event.preventDefault();
          const data = new FormData(event.currentTarget);
          const common: CreateSiteRequest = {
            code: String(data.get('code') ?? '').trim(),
            name: String(data.get('name') ?? '').trim(),
            timezone: String(data.get('timezone') ?? '').trim(),
          };
          onSave(site && site !== 'new'
            ? { ...common, enabled: data.get('enabled') === 'on', rowVersion: site.rowVersion }
            : common);
        }}
      >
        <DialogTitle>{site === 'new' ? 'Create Site' : 'Edit Site'}</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            <MutationError error={error} conflictLabel="Site update conflict" onReload={onReload} />
            <TextField name="code" label="Site code" defaultValue={site !== 'new' ? site?.code : ''} required />
            <TextField name="name" label="Site name" defaultValue={site !== 'new' ? site?.name : ''} required />
            <TextField
              name="timezone"
              label="IANA timezone"
              defaultValue={site !== 'new' ? site?.timezone : 'Asia/Bangkok'}
              required
              helperText="For example, Asia/Bangkok"
            />
            {site && site !== 'new' && (
              <FormControlLabel control={<Switch name="enabled" defaultChecked={site.enabled} />} label="Enabled" />
            )}
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={saving}>Cancel</Button>
          <Button type="submit" variant="contained" disabled={saving}>{saving ? 'Saving…' : 'Save'}</Button>
        </DialogActions>
      </Box>
    </Dialog>
  );
}
