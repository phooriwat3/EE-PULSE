import {
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  FormControlLabel,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  Switch,
  TextField,
} from '@mui/material';
import type {
  CreateDeviceRequest,
  DeviceResponse,
  SiteResponse,
  UpdateDeviceRequest,
} from '../../api/contracts';
import { MutationError } from '../feedback';

export function DeviceDialog({ device, sites, open, saving, error, onClose, onReload, onSave }: {
  device: DeviceResponse | 'new' | null;
  sites: SiteResponse[];
  open: boolean;
  saving: boolean;
  error: unknown;
  onClose: () => void;
  onReload: () => void;
  onSave: (request: CreateDeviceRequest | UpdateDeviceRequest) => void;
}) {
  const key = device === 'new' ? 'new' : (device?.id ?? 'closed');
  return (
    <Dialog key={key} open={open} onClose={saving ? undefined : onClose} fullWidth maxWidth="md">
      <Box
        component="form"
        onSubmit={(event) => {
          event.preventDefault();
          const data = new FormData(event.currentTarget);
          const nullable = (name: string) => String(data.get(name) ?? '').trim() || null;
          const common: CreateDeviceRequest = {
            siteId: String(data.get('siteId') ?? ''),
            name: String(data.get('name') ?? '').trim(),
            address: String(data.get('address') ?? '').trim(),
            hostname: nullable('hostname'),
            deviceType: String(data.get('deviceType') ?? '').trim(),
            area: nullable('area'),
            owner: nullable('owner'),
            criticality: String(data.get('criticality') ?? ''),
            tags: String(data.get('tags') ?? '').split(',').map((tag) => tag.trim()).filter(Boolean),
          };
          onSave(device && device !== 'new'
            ? { ...common, enabled: data.get('enabled') === 'on', rowVersion: device.rowVersion }
            : common);
        }}
      >
        <DialogTitle>{device === 'new' ? 'Create Device' : 'Edit Device'}</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            <MutationError error={error} conflictLabel="Device update conflict" onReload={onReload} />
            <FormControl required fullWidth>
              <InputLabel id="device-site-label">Site</InputLabel>
              <Select
                name="siteId"
                labelId="device-site-label"
                label="Site"
                defaultValue={device !== 'new' ? (device?.siteId ?? '') : ''}
              >
                {sites.filter((site) => site.enabled || site.id === (device !== 'new' ? device?.siteId : '')).map((site) => (
                  <MenuItem key={site.id} value={site.id}>{site.name} ({site.code})</MenuItem>
                ))}
              </Select>
            </FormControl>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField name="name" label="Device name" defaultValue={device !== 'new' ? device?.name : ''} required fullWidth />
              <TextField name="address" label="IPv4 address" defaultValue={device !== 'new' ? device?.address : ''} required fullWidth />
            </Stack>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField name="hostname" label="Hostname (optional)" defaultValue={device !== 'new' ? device?.hostname : ''} fullWidth />
              <TextField name="deviceType" label="Device type" defaultValue={device !== 'new' ? device?.deviceType : ''} required fullWidth />
            </Stack>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField name="area" label="Area (optional)" defaultValue={device !== 'new' ? device?.area : ''} fullWidth />
              <TextField name="owner" label="Owner (optional)" defaultValue={device !== 'new' ? device?.owner : ''} fullWidth />
            </Stack>
            <FormControl required fullWidth>
              <InputLabel id="device-criticality-label">Criticality</InputLabel>
              <Select
                name="criticality"
                labelId="device-criticality-label"
                label="Criticality"
                defaultValue={device !== 'new' ? (device?.criticality ?? 'Normal') : 'Normal'}
              >
                {['Low', 'Normal', 'High', 'Critical'].map((value) => <MenuItem key={value} value={value}>{value}</MenuItem>)}
              </Select>
            </FormControl>
            <TextField
              name="tags"
              label="Tags"
              defaultValue={device !== 'new' ? device?.tags.join(', ') : ''}
              helperText="Comma-separated"
            />
            {device && device !== 'new' && (
              <FormControlLabel control={<Switch name="enabled" defaultChecked={device.enabled} />} label="Enabled" />
            )}
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={saving}>Cancel</Button>
          <Button type="submit" variant="contained" disabled={saving || sites.length === 0}>
            {saving ? 'Saving…' : 'Save Device'}
          </Button>
        </DialogActions>
      </Box>
    </Dialog>
  );
}
