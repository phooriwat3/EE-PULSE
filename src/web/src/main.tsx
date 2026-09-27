import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createTheme, ThemeProvider } from '@mui/material/styles';
import { App } from './App';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 10_000,
      retry: 1,
    },
  },
});

const theme = createTheme({
  palette: {
    primary: { main: '#a84b1c', contrastText: '#ffffff' },
    secondary: { main: '#303843' },
    background: { default: '#f5f6f8' },
  },
  typography: { fontFamily: '"Segoe UI", Arial, sans-serif', button: { textTransform: 'none', fontWeight: 600 } },
  shape: { borderRadius: 5 },
  components: { MuiAppBar: { styleOverrides: { root: { backgroundColor: '#252b33', boxShadow: 'none', borderBottom: '3px solid #e86624' } } } },
});

const rootElement = document.getElementById('root');

if (!rootElement) {
  throw new Error('EE Pulse root element was not found');
}

createRoot(rootElement).render(
  <StrictMode>
    <ThemeProvider theme={theme}>
      <QueryClientProvider client={queryClient}>
        <App />
      </QueryClientProvider>
    </ThemeProvider>
  </StrictMode>,
);
