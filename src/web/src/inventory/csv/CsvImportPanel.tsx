import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Chip,
  LinearProgress,
  Paper,
  Stack,
  Typography,
} from '@mui/material';
import type { DevelopmentRole } from '../../api/client';
import type { CsvImportPreviewRow } from '../../api/contracts';
import { inventoryApi } from '../../api/inventory';
import { MutationError } from '../feedback';

export function CsvImportPanel({ canWrite, actorRole }: { canWrite: boolean; actorRole: DevelopmentRole }) {
  const queryClient = useQueryClient();
  const [csv, setCsv] = useState('');
  const [fileName, setFileName] = useState('');
  const [fileError, setFileError] = useState('');
  const preview = useMutation({ mutationFn: inventoryApi.previewCsv });
  const commit = useMutation({
    mutationFn: inventoryApi.commitCsv,
    onSuccess: async () => queryClient.invalidateQueries({ queryKey: ['devices'] }),
  });

  if (!canWrite) {
    return (
      <Alert severity="error">
        <strong>Permission denied.</strong> CSV preview and commit require Engineer or Administrator; current role is {actorRole}.
      </Alert>
    );
  }

  const result = preview.data;
  const reset = () => {
    preview.reset();
    commit.reset();
    setCsv('');
    setFileName('');
    setFileError('');
  };

  return (
    <Stack spacing={3}>
      <Box>
        <Typography variant="h5" component="h3">CSV Device import</Typography>
        <Typography color="text.secondary">
          Upload, review normalized rows and server validation, then explicitly commit valid rows.
        </Typography>
      </Box>
      <Paper variant="outlined" sx={{ p: 3 }}>
        <Stack spacing={2}>
          <Typography variant="h6" component="h4">1. Select CSV</Typography>
          <Typography variant="body2" color="text.secondary">
            Required header order: siteCode, name, address, hostname, deviceType, area, owner, criticality, tags.
            Separate tags with a vertical bar.
          </Typography>
          <Button component="label" variant="outlined">
            Choose CSV file
            <input
              hidden
              type="file"
              accept=".csv,text/csv"
              onChange={async (event) => {
                const file = event.target.files?.[0];
                if (!file) return;
                try {
                  setCsv(await file.text());
                  setFileName(file.name);
                  setFileError('');
                  preview.reset();
                  commit.reset();
                } catch {
                  setFileError('The selected file could not be read.');
                }
              }}
            />
          </Button>
          {fileName && <Typography>Selected: {fileName}</Typography>}
          {fileError && <Alert severity="error">{fileError}</Alert>}
          <MutationError error={preview.error} />
          {preview.isPending && <LinearProgress aria-label="Previewing CSV" />}
          <Stack direction="row" spacing={1}>
            <Button
              variant="contained"
              onClick={() => preview.mutate(csv)}
              disabled={!csv || preview.isPending}
            >
              Preview import
            </Button>
            {(csv || result) && <Button onClick={reset}>Start over</Button>}
          </Stack>
        </Stack>
      </Paper>

      {result && (
        <Paper variant="outlined" sx={{ p: 3 }}>
          <Stack spacing={2}>
            <Typography variant="h6" component="h4">2. Review server validation</Typography>
            <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap' }}>
              <Chip label={`${String(result.totalRows)} total rows`} variant="outlined" />
              <Chip label={`${String(result.validRows)} valid rows`} color="success" variant="outlined" />
              <Chip label={`${String(result.invalidRows)} invalid rows`} color={Number(result.invalidRows) ? 'error' : 'default'} variant="outlined" />
            </Stack>
            <Typography variant="body2" color="text.secondary">
              Preview expires {new Date(result.expiresAt).toLocaleString()}.
            </Typography>
            {result.rows.length === 0 ? (
              <Alert severity="info">The CSV contains no data rows.</Alert>
            ) : (
              <Stack spacing={1} aria-label="CSV preview rows">
                {result.rows.map((row) => <PreviewRow key={String(row.rowNumber)} row={row} />)}
              </Stack>
            )}
          </Stack>
        </Paper>
      )}

      {result && (
        <Paper variant="outlined" sx={{ p: 3 }}>
          <Stack spacing={2}>
            <Typography variant="h6" component="h4">3. Commit valid rows</Typography>
            <Typography color="text.secondary">
              Invalid rows will be skipped. The API rechecks the stored preview during commit.
            </Typography>
            <MutationError error={commit.error} conflictLabel="Import conflict" />
            {commit.isPending && <LinearProgress aria-label="Committing CSV import" />}
            {commit.data && (
              <Alert severity={Number(commit.data.errors.length) ? 'warning' : 'success'}>
                {commit.data.alreadyCommitted ? 'This preview was already committed. ' : ''}
                Created {String(commit.data.created)} Devices; skipped {String(commit.data.skipped)} rows.
              </Alert>
            )}
            {commit.data && commit.data.errors.length > 0 && (
              <Stack spacing={1} aria-label="CSV commit errors">
                {commit.data.errors.map((row) => <PreviewRow key={`commit-${String(row.rowNumber)}`} row={row} />)}
              </Stack>
            )}
            <Button
              variant="contained"
              onClick={() => commit.mutate(result.previewToken)}
              disabled={commit.isPending || Number(result.validRows) === 0}
              sx={{ alignSelf: 'flex-start' }}
            >
              Commit {String(result.validRows)} valid rows
            </Button>
          </Stack>
        </Paper>
      )}
    </Stack>
  );
}

function PreviewRow({ row }: { row: CsvImportPreviewRow }) {
  const valid = row.errors.length === 0;
  return (
    <Paper variant="outlined" sx={{ p: 2 }} component="article">
      <Stack spacing={0.5}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          <Typography variant="subtitle1" component="h5">Row {String(row.rowNumber)}</Typography>
          <Chip
            label={valid ? 'Valid' : `Invalid: ${row.errors.length} error${row.errors.length === 1 ? '' : 's'}`}
            color={valid ? 'success' : 'error'}
            size="small"
            variant="outlined"
          />
        </Stack>
        {row.normalized && (
          <Typography variant="body2">
            {row.normalized.siteCode} · {row.normalized.name} · {row.normalized.address} · {row.normalized.deviceType} · {row.normalized.criticality}
          </Typography>
        )}
        {row.errors.map((error, index) => (
          <Alert key={`${error.field}-${error.code}-${index}`} severity="error" variant="outlined">
            <strong>{error.field}</strong> ({error.code}): {error.message}
          </Alert>
        ))}
      </Stack>
    </Paper>
  );
}
