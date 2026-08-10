import { useState } from 'react';
import { Box, Container, Stack, Tab, Tabs, Typography } from '@mui/material';
import type { DevelopmentSession } from '../api/client';
import { CsvImportPanel } from './csv/CsvImportPanel';
import { DevicePanel } from './devices/DevicePanel';
import { SitePanel } from './sites/SitePanel';

interface Props {
  session: DevelopmentSession;
  canWrite: boolean;
  canManageSites: boolean;
}

export function InventoryWorkspace({ session, canWrite, canManageSites }: Props) {
  const [tab, setTab] = useState(0);
  return (
    <Container component="main" maxWidth="xl" sx={{ py: { xs: 3, md: 5 } }}>
      <Stack spacing={3}>
        <Box>
          <Typography variant="h4" component="h2" gutterBottom>Inventory</Typography>
          <Typography color="text.secondary">
            Manage Sites, Devices, ICMP Probe settings, and reviewed CSV imports.
          </Typography>
        </Box>
        <Tabs
          value={tab}
          onChange={(_, value: number) => setTab(value)}
          aria-label="Inventory sections"
          variant="scrollable"
          allowScrollButtonsMobile
        >
          <Tab label="Devices" id="inventory-tab-0" aria-controls="inventory-panel-0" />
          <Tab label="Sites" id="inventory-tab-1" aria-controls="inventory-panel-1" />
          <Tab label="CSV import" id="inventory-tab-2" aria-controls="inventory-panel-2" />
        </Tabs>
        <Box role="tabpanel" id={`inventory-panel-${tab}`} aria-labelledby={`inventory-tab-${tab}`}>
          {tab === 0 && <DevicePanel canWrite={canWrite} />}
          {tab === 1 && <SitePanel canManage={canManageSites} />}
          {tab === 2 && <CsvImportPanel canWrite={canWrite} actorRole={session.role} />}
        </Box>
      </Stack>
    </Container>
  );
}
