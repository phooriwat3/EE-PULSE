import { useEffect, useMemo, useState } from 'react';
import {
  AppBar,
  Box,
  Button,
  Container,
  CssBaseline,
  FormControl,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  Toolbar,
  Typography,
} from '@mui/material';
import type { DevelopmentRole, DevelopmentSession } from './api/client';
import { setApiSession } from './api/client';
import { InventoryWorkspace } from './inventory/InventoryWorkspace';

const roles: DevelopmentRole[] = ['Viewer', 'Operator', 'Engineer', 'Administrator', 'Auditor'];
const syntheticActorId = '00000000-0000-4000-8000-000000000002';

export function App() {
  if (!import.meta.env.DEV) return <ProductionAuthenticationRequired />;
  return <DevelopmentApp />;
}

export function ProductionAuthenticationRequired() {
  return (
    <>
      <CssBaseline />
      <AppBar position="static">
        <Toolbar><Typography component="h1" variant="h6">EE Pulse</Typography></Toolbar>
      </AppBar>
      <Container component="main" maxWidth="sm" sx={{ py: 8 }}>
        <Stack spacing={2} component="section" aria-labelledby="authentication-required-heading">
          <Typography id="authentication-required-heading" variant="h4" component="h2">
            Authentication required
          </Typography>
          <Typography color="text.secondary">
            OIDC authentication is not configured for this deployment. Access is denied until an administrator configures it.
          </Typography>
        </Stack>
      </Container>
    </>
  );
}

function DevelopmentApp() {
  const [session, setSession] = useState<DevelopmentSession | null>(null);
  const [selectedRole, setSelectedRole] = useState<DevelopmentRole>('Viewer');
  useEffect(() => {
    setApiSession(session);
  }, [session]);
  useEffect(() => () => setApiSession(null), []);

  const capabilities = useMemo(
    () => ({
      canWrite: session?.role === 'Engineer' || session?.role === 'Administrator',
      canManageSites: session?.role === 'Administrator',
    }),
    [session],
  );

  const signIn = () => {
    const privileged = selectedRole === 'Engineer' || selectedRole === 'Administrator';
    const nextSession = { role: selectedRole, actorId: privileged ? syntheticActorId : undefined };
    setApiSession(nextSession);
    setSession(nextSession);
  };

  const signOut = () => {
    setApiSession(null);
    setSession(null);
  };

  return (
    <>
      <CssBaseline />
      <AppBar position="static">
        <Toolbar sx={{ gap: 2 }}>
          <Box sx={{ flexGrow: 1 }}>
            <Typography component="h1" variant="h6">EE Pulse</Typography>
            <Typography variant="caption" sx={{ opacity: 0.85 }}>Inventory console</Typography>
          </Box>
          {session && (
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
              <Typography variant="body2">Signed in: {session.role}</Typography>
              <Button color="inherit" variant="outlined" onClick={signOut}>Sign out</Button>
            </Stack>
          )}
        </Toolbar>
      </AppBar>

      {!session ? (
        <Container component="main" maxWidth="sm" sx={{ py: 8 }}>
          <Stack spacing={3} component="section" aria-labelledby="sign-in-heading">
            <Box>
              <Typography id="sign-in-heading" variant="h4" component="h2" gutterBottom>
                Development access
              </Typography>
              <Typography color="text.secondary">
                Choose a synthetic role for the local API. Production authentication will use OIDC.
              </Typography>
            </Box>
            <FormControl fullWidth>
              <InputLabel id="role-label">Role</InputLabel>
              <Select
                labelId="role-label"
                value={selectedRole}
                label="Role"
                onChange={(event) => setSelectedRole(event.target.value as DevelopmentRole)}
              >
                {roles.map((role) => <MenuItem key={role} value={role}>{role}</MenuItem>)}
              </Select>
            </FormControl>
            <Button variant="contained" size="large" onClick={signIn}>Use synthetic role</Button>
          </Stack>
        </Container>
      ) : (
        <InventoryWorkspace session={session} {...capabilities} />
      )}
    </>
  );
}
