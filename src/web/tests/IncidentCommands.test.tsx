import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { IncidentCommands } from '../src/dashboard/IncidentCommands';
import { incident } from './fixtures/operations';
import type { DevelopmentRole } from '../src/api/client';
import { createCommandIntent } from '../src/api/incidentCommands';

const snapshot = { incident, etag: '"current"' };
function response(body: unknown = {}, status = 201) { return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }); }
function setup(role: DevelopmentRole = 'Operator') {
  const props = { snapshot, role, ready: true, probeStatus: 'Up' as const, onRefresh: vi.fn(async () => {}), onSuccess: vi.fn(async () => {}), onProtectedChange: vi.fn() };
  return { ...render(<IncidentCommands {...props} />), props };
}
function comment(value = '  Operator comment 😀  ') { fireEvent.change(screen.getByRole('textbox', { name: 'Comment' }), { target: { value } }); }
function choose(label: string) { fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Action' })); fireEvent.click(screen.getByRole('option', { name: label })); }
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
it.each(['Viewer', 'Engineer', 'Auditor'] as const)('keeps %s read-only', role => {
  setup(role); expect(screen.getByText(/Read-only access/)).toBeVisible(); expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
});
it.each(['Operator', 'Administrator'] as const)('shows commands for %s and validates before any request', role => {
  const fetcher = vi.fn(); vi.stubGlobal('fetch', fetcher); setup(role);
  fireEvent.click(screen.getByRole('button', { name: 'Submit comment' }));
  expect(screen.getByText('Enter a comment or note.')).toBeVisible(); expect(fetcher).not.toHaveBeenCalled();
  comment('x'.repeat(2001)); fireEvent.click(screen.getByRole('button', { name: 'Submit comment' }));
  expect(screen.getByText(/Use at most 2,000/)).toBeVisible(); expect(fetcher).not.toHaveBeenCalled();
});
it('prevents duplicate submission, locks pending input and confirms success before refreshing', async () => {
  let finish!: (value: Response) => void;
  const fetcher = vi.fn(() => new Promise<Response>(resolve => { finish = resolve; })); vi.stubGlobal('fetch', fetcher);
  const { props } = setup(); comment();
  const submit = screen.getByRole('button', { name: 'Submit comment' }); fireEvent.click(submit); fireEvent.click(submit);
  expect(fetcher).toHaveBeenCalledTimes(1); expect(screen.getByRole('textbox')).toBeDisabled(); expect(props.onSuccess).not.toHaveBeenCalled();
  finish(response()); await screen.findByText(/Request confirmed/);
  expect(props.onSuccess).toHaveBeenCalledTimes(1); expect(screen.getByRole('textbox')).toHaveValue('');
});
it.each([503, 502, 500])('preserves intent and text for explicit exact retry after unconfirmed HTTP %s', async status => {
  const fetcher = vi.fn().mockResolvedValueOnce(response({ detail: 'PRIVATE exception' }, status)).mockResolvedValueOnce(response()); vi.stubGlobal('fetch', fetcher);
  setup(); comment(); fireEvent.click(screen.getByRole('button', { name: 'Submit comment' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Retry saved request' }));
  await screen.findByText(/Request confirmed/);
  const calls = fetcher.mock.calls as [string, RequestInit][];
  expect(calls[1][1].body).toBe(calls[0][1].body); expect(new Headers(calls[1][1].headers).get('Idempotency-Key')).toBe(new Headers(calls[0][1].headers).get('Idempotency-Key'));
  expect(new Headers(calls[1][1].headers).get('If-Match')).toBe('"current"'); expect(screen.queryByText(/PRIVATE/)).not.toBeInTheDocument();
});
it.each([
  [412, 'concurrency-conflict', 'This incident changed.'],
  [409, 'incident-action-state-conflict', 'This action is not allowed'],
  [409, 'incident-manual-resolution-state-conflict', 'Resolution is not allowed'],
  [409, 'idempotency-key-reuse-conflict', 'This saved request conflicts'],
] as const)('distinguishes %s %s, retains input and requires refresh/new explicit submission', async (status, code, text) => {
  const fetcher = vi.fn().mockResolvedValueOnce(response({ code }, status)).mockResolvedValueOnce(response()); vi.stubGlobal('fetch', fetcher);
  const { props } = setup(); comment(); fireEvent.click(screen.getByRole('button', { name: 'Submit comment' }));
  await screen.findByText(new RegExp(text)); expect(screen.getByRole('textbox')).toHaveValue('  Operator comment 😀  ');
  expect(screen.getByRole('button', { name: 'Submit comment' })).toBeDisabled();
  comment('Changed text'); expect(screen.getByRole('button', { name: 'Submit comment' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Refresh incident' }));
  await waitFor(() => expect(props.onRefresh).toHaveBeenCalledTimes(1));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Submit comment' })).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Submit comment' })); await screen.findByText(/Request confirmed/);
  expect(new Headers(fetcher.mock.calls[1][1].headers).get('Idempotency-Key')).not.toBe(new Headers(fetcher.mock.calls[0][1].headers).get('Idempotency-Key'));
});
it.each([400, 428])('keeps text after request validation %s without echoing server detail', async status => {
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response({ detail: 'private' }, status)))); setup(); comment();
  fireEvent.click(screen.getByRole('button', { name: 'Submit comment' })); await screen.findByText(/request was not accepted/);
  expect(screen.getByRole('textbox')).toHaveValue('  Operator comment 😀  '); expect(screen.queryByText('private')).not.toBeInTheDocument();
});
it.each([401, 403])('leaves server authorization authoritative after %s', async status => {
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response({}, status)))); setup(); comment();
  fireEvent.click(screen.getByRole('button', { name: 'Submit comment' })); await screen.findByText(/not authorized/);
  expect(screen.getByRole('textbox')).toBeDisabled(); expect(screen.queryByRole('button', { name: 'Retry saved request' })).not.toBeInTheDocument();
});
it('requires explicit resolution confirmation and uses note, not comment', async () => {
  const fetcher = vi.fn(() => Promise.resolve(response({}, 200))); vi.stubGlobal('fetch', fetcher); setup(); choose('Resolve');
  fireEvent.change(screen.getByRole('textbox', { name: 'Resolution note' }), { target: { value: ' Verified recovery ' } });
  fireEvent.click(screen.getByRole('button', { name: 'Review resolution' })); expect(fetcher).not.toHaveBeenCalled();
  expect(screen.getByRole('dialog', { name: 'Confirm incident resolution' })).toBeVisible();
  expect(screen.getByRole('button', { name: 'Back' })).toHaveFocus();
  fireEvent.click(screen.getByRole('button', { name: 'Confirm resolution' })); await screen.findByText(/Request confirmed/);
  expect((fetcher.mock.calls[0] as unknown as [string, RequestInit])[1].body).toBe('{"note":" Verified recovery "}');
});
it('does not infer rollback from cancellation; exact retry remains available', async () => {
  const fetcher = vi.fn((_: string, init: RequestInit) => new Promise<Response>((_, reject) => init.signal!.addEventListener('abort', () => reject(new DOMException('Stopped', 'AbortError')))));
  vi.stubGlobal('fetch', fetcher); setup(); comment(); fireEvent.click(screen.getByRole('button', { name: 'Submit comment' }));
  fireEvent.click(screen.getByRole('button', { name: 'Stop waiting' })); await screen.findByText(/may have completed on the server/);
  expect(screen.getByRole('button', { name: 'Retry saved request' })).toBeEnabled(); expect(screen.getByRole('textbox')).toHaveValue('  Operator comment 😀  ');
});
it('aborts and removes saved text/command state on identity remount', async () => {
  let signal!: AbortSignal;
  vi.stubGlobal('fetch', vi.fn((_: string, init: RequestInit) => { signal = init.signal!; return new Promise<Response>((_, reject) => signal.addEventListener('abort', () => reject(new DOMException('Stopped', 'AbortError')))); }));
  const { rerender, props } = setup(); comment(); fireEvent.click(screen.getByRole('button', { name: 'Submit comment' }));
  rerender(<IncidentCommands key="new-identity" {...props} />);
  expect(signal.aborted).toBe(true); expect(screen.getByRole('textbox')).toHaveValue(''); expect(screen.queryByRole('button', { name: 'Retry saved request' })).not.toBeInTheDocument();
});
it('blocks resolution while probe state is missing/Down/Recovering and does not restrict comments on resolved incidents', () => {
  const { rerender, props } = setup(); choose('Resolve');
  for (const probeStatus of [undefined, 'Down', 'Recovering'] as const) {
    rerender(<IncidentCommands {...props} probeStatus={probeStatus} />); expect(screen.getByRole('button', { name: 'Review resolution' })).toBeDisabled();
  }
  choose('Add comment'); rerender(<IncidentCommands {...props} snapshot={{ ...snapshot, incident: { ...incident, status: 'Resolved' } }} />);
  expect(screen.getByRole('button', { name: 'Submit comment' })).toBeEnabled();
});
it('requires deliberate discard before a new request after an uncertain outcome', async () => {
  const fetcher = vi.fn(() => Promise.reject(new TypeError('connection lost'))); vi.stubGlobal('fetch', fetcher); setup(); comment();
  fireEvent.click(screen.getByRole('button', { name: 'Submit comment' })); await screen.findByRole('button', { name: 'Retry saved request' });
  fireEvent.click(screen.getByRole('button', { name: 'Discard saved request' })); expect(screen.getByRole('dialog', { name: 'Discard the saved request?' })).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Discard and review' })); await waitFor(() => expect(screen.getByRole('button', { name: 'Submit comment' })).toBeDisabled());
  expect(fetcher).toHaveBeenCalledTimes(1); expect(createCommandIntent('comments', incident.id, '"e"', 'changed').body).toBe('{"comment":"changed"}');
});

it('guards refresh synchronously and disables new requests/input until completion', async () => {
  let finish!: () => void;
  const props = setup().props;
  cleanup();
  props.onRefresh = vi.fn(() => new Promise<void>(resolve => { finish = resolve; }));
  render(<IncidentCommands {...props} />);
  const fetcher = vi.fn(); vi.stubGlobal('fetch', fetcher); comment('Preserved draft');
  const button = screen.getByRole('button', { name: 'Refresh incident' }); fireEvent.click(button); fireEvent.click(button);
  expect(props.onRefresh).toHaveBeenCalledTimes(1); expect(screen.getByRole('textbox')).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Submit comment' })); expect(fetcher).not.toHaveBeenCalled();
  finish(); await waitFor(() => expect(screen.getByRole('button', { name: 'Submit comment' })).toBeEnabled());
  expect(screen.getByRole('textbox')).toHaveValue('Preserved draft');
});
it('does not reinterpret confirmed success as an uncertain command when refresh fails', async () => {
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response())));
  const props = setup().props; cleanup(); props.onSuccess = vi.fn(() => Promise.reject(new Error('refresh failed')));
  render(<IncidentCommands {...props} />); comment(); fireEvent.click(screen.getByRole('button', { name: 'Submit comment' }));
  await screen.findByText(/Request confirmed, but the latest snapshot/); expect(screen.queryByRole('button', { name: 'Retry saved request' })).not.toBeInTheDocument();
});
it('keeps read detail usable but disables commands without its authoritative strong ETag', () => {
  const props = setup().props; cleanup(); render(<IncidentCommands {...props} snapshot={{ ...snapshot, etag: null }} />);
  expect(screen.getByText(/incident version is unavailable/)).toBeVisible(); expect(screen.getByRole('button', { name: 'Submit comment' })).toBeDisabled();
});
