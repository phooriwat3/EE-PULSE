import { useQuery } from '@tanstack/react-query';
import {
  Alert,
  AppBar,
  Box,
  Chip,
  Container,
  CssBaseline,
  Paper,
  Stack,
  Toolbar,
  Typography,
} from '@mui/material';
import { getApiHealth } from './api/health';

export function App() {
  const health = useQuery({
    queryKey: ['api-health'],
    queryFn: ({ signal }) => getApiHealth(signal),
    refetchInterval: 30_000,
  });

  return (
    <>
      <CssBaseline />
      <AppBar position="static">
        <Toolbar>
          <Typography component="h1" variant="h6">
            EE Pulse
          </Typography>
        </Toolbar>
      </AppBar>
      <Container component="main" maxWidth="md" sx={{ py: 6 }}>
        <Stack spacing={3}>
          <Box>
            <Typography variant="h4" component="h2" gutterBottom>
              Monitoring foundation
            </Typography>
            <Typography color="text.secondary">
              The WP-01 shell verifies the versioned API health contract. Device monitoring pages arrive in WP-07.
            </Typography>
          </Box>

          <Paper variant="outlined" sx={{ p: 3 }}>
            <Stack
              direction={{ xs: 'column', sm: 'row' }}
              spacing={2}
              sx={{ alignItems: { sm: 'center' } }}
            >
              <Typography variant="h6" component="h3" sx={{ flexGrow: 1 }}>
                Central API
              </Typography>
              {health.isPending && <Chip label="Checking" variant="outlined" />}
              {health.isSuccess && <Chip label="Ready" color="success" />}
              {health.isError && <Chip label="Unavailable" color="error" />}
            </Stack>

            {health.isSuccess && (
              <Typography sx={{ mt: 2 }}>
                {health.data.service} contract v{health.data.schemaVersion}, checked{' '}
                {new Date(health.data.checkedAt).toLocaleString()}
              </Typography>
            )}

            {health.isError && (
              <Alert severity="warning" sx={{ mt: 2 }}>
                The API health endpoint is unavailable. Start the API or Docker Compose stack and retry.
              </Alert>
            )}
          </Paper>
        </Stack>
      </Container>
    </>
  );
}
