import { Alert, Button, CircularProgress, Stack, Typography } from '@mui/material';
import { ApiError } from '../api/client';

export function LoadingState({ label }: { label: string }) {
  return (
    <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }} role="status">
      <CircularProgress size={22} />
      <Typography>{label}</Typography>
    </Stack>
  );
}

export function ErrorState({ error, retry }: { error: unknown; retry: () => void }) {
  const apiError = error instanceof ApiError ? error : undefined;
  const severity = apiError?.status === 403 ? 'error' : 'warning';
  const heading = apiError?.status === 401
    ? 'Authentication required'
    : apiError?.status === 403
      ? 'Permission denied'
      : 'Unable to load data';
  return (
    <Alert
      severity={severity}
      action={<Button color="inherit" size="small" onClick={retry}>Retry</Button>}
    >
      <strong>{heading}.</strong> {apiError?.message ?? 'Check the local API and try again.'}
    </Alert>
  );
}

export function MutationError({ error, conflictLabel = 'Save conflict', onReload }: {
  error: unknown;
  conflictLabel?: string;
  onReload?: () => void;
}) {
  if (!error) return null;
  const apiError = error instanceof ApiError ? error : undefined;
  const conflict = apiError?.status === 409;
  return (
    <Alert
      severity="error"
      role="alert"
      action={conflict && onReload
        ? <Button color="inherit" size="small" onClick={onReload}>Reload latest</Button>
        : undefined}
    >
      <strong>{conflict ? conflictLabel : 'Validation or request error'}.</strong>{' '}
      {apiError?.message ?? 'The request could not be completed.'}
      {conflict && ' Reload the latest data before trying again.'}
    </Alert>
  );
}
