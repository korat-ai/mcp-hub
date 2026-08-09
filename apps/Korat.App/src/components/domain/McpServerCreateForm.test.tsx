/**
 * Unit tests for McpServerCreateForm (Increment 1, HTTP MCP direct-to-Space — Task 7).
 *
 * Mirrors the test-harness pattern established for InferenceCreateForms.tsx's ByoEndpointTab
 * (see routes/inference.new.test.tsx): mock at the `@/lib/api` boundary, mount the real
 * component under a QueryClientProvider (no router needed — onCreated is a plain prop, not a
 * navigate call owned by this component).
 *
 * Covers:
 *  - Header name + secret fields are hidden for the default 'none' auth mode.
 *  - Selecting 'header' reveals both the header-name and secret fields.
 *  - Selecting 'bearer' reveals only the secret field (no header-name field).
 *  - Submit (none auth) calls api.mcpServers.create with the expected body — no
 *    authHeaderName/secret keys included.
 *  - Submit (header auth) calls api.mcpServers.create with authHeaderName + secret included.
 *  - On success, onCreated is called with the created server's id.
 *  - On a 409 failure, the mapped inline error is shown (not a raw ApiError message).
 *
 *  "Auto-detect auth mode" feature (mocks api.mcpServers.detectAuth):
 *  - Blurring the Remote URL field calls detectAuth and, on 'oauth', selects the Auth dropdown
 *    (the secret field stays hidden, matching the oauth-mode field-visibility guard above).
 *  - An 'unknown' result leaves the current selection untouched and shows a manual-pick hint.
 *  - Re-blurring the same URL does not call detectAuth a second time.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const mockCreate = vi.fn();
const mockDetectAuth = vi.fn();

vi.mock('@/lib/api', () => ({
  api: {
    mcpServers: {
      create: (...args: unknown[]) => mockCreate(...args),
      detectAuth: (...args: unknown[]) => mockDetectAuth(...args),
    },
  },
  ApiError: class ApiError extends Error {
    constructor(public status: number, public body: string) {
      super(`HTTP ${status}`);
    }
  },
}));

vi.mock('@/lib/toast', () => ({
  toastReceipt: vi.fn(),
}));

import { McpServerCreateForm } from './McpServerCreateForm';
import { ApiError } from '@/lib/api';

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

function renderForm(onCreated: (serverId: string) => void = vi.fn()) {
  const qc = makeQC();
  render(
    <QueryClientProvider client={qc}>
      <McpServerCreateForm onCreated={onCreated} />
    </QueryClientProvider>,
  );
  return { onCreated };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('McpServerCreateForm — auth mode field visibility', () => {
  it('hides header-name and secret fields for the default None auth mode', () => {
    renderForm();
    expect(screen.queryByLabelText(/header name/i)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/secret/i)).not.toBeInTheDocument();
  });

  it('shows both header-name and secret fields when auth mode is Custom header', () => {
    renderForm();
    fireEvent.change(screen.getByLabelText(/auth/i), { target: { value: 'header' } });
    expect(screen.getByLabelText(/header name/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/secret/i)).toBeInTheDocument();
  });

  it('shows only the secret field (no header-name field) when auth mode is Bearer token', () => {
    renderForm();
    fireEvent.change(screen.getByLabelText(/auth/i), { target: { value: 'bearer' } });
    expect(screen.queryByLabelText(/header name/i)).not.toBeInTheDocument();
    expect(screen.getByLabelText(/secret/i)).toBeInTheDocument();
  });
});

describe('McpServerCreateForm — submit (None auth)', () => {
  it('calls api.mcpServers.create with the expected body, no authHeaderName/secret keys', async () => {
    mockCreate.mockResolvedValue({ id: 'srv_123', displayName: 'my-server', transport: 'http_cloud' });
    const { onCreated } = renderForm();

    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: 'my-server' } });
    fireEvent.change(screen.getByLabelText(/remote url/i), { target: { value: 'https://example.test/mcp' } });

    fireEvent.click(screen.getByRole('button', { name: /add http mcp server/i }));

    await waitFor(() => expect(mockCreate).toHaveBeenCalledTimes(1));
    const body = mockCreate.mock.calls[0][0];
    expect(body).toEqual({
      displayName: 'my-server',
      remoteUrl: 'https://example.test/mcp',
      authMode: 'none',
    });
    expect(body).not.toHaveProperty('authHeaderName');
    expect(body).not.toHaveProperty('secret');

    await waitFor(() => expect(onCreated).toHaveBeenCalledWith('srv_123'));
  });
});

describe('McpServerCreateForm — submit (header auth)', () => {
  it('calls api.mcpServers.create with authHeaderName + secret included', async () => {
    mockCreate.mockResolvedValue({ id: 'srv_456', displayName: 'hdr-server', transport: 'http_cloud' });
    renderForm();

    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: 'hdr-server' } });
    fireEvent.change(screen.getByLabelText(/remote url/i), { target: { value: 'https://example.test/mcp' } });
    fireEvent.change(screen.getByLabelText(/auth/i), { target: { value: 'header' } });
    fireEvent.change(screen.getByLabelText(/header name/i), { target: { value: 'X-Api-Key' } });
    fireEvent.change(screen.getByLabelText(/secret/i), { target: { value: 'the-secret-value' } });

    fireEvent.click(screen.getByRole('button', { name: /add http mcp server/i }));

    await waitFor(() => expect(mockCreate).toHaveBeenCalledTimes(1));
    const body = mockCreate.mock.calls[0][0];
    expect(body).toEqual({
      displayName: 'hdr-server',
      remoteUrl: 'https://example.test/mcp',
      authMode: 'header',
      authHeaderName: 'X-Api-Key',
      secret: 'the-secret-value',
    });
  });
});

describe('McpServerCreateForm — submit gating', () => {
  it('the submit button stays disabled until name + remote URL are filled in', () => {
    renderForm();
    const submitBtn = screen.getByRole('button', { name: /add http mcp server/i });
    expect(submitBtn).toBeDisabled();

    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: 'my-server' } });
    expect(submitBtn).toBeDisabled();

    fireEvent.change(screen.getByLabelText(/remote url/i), { target: { value: 'https://example.test/mcp' } });
    expect(submitBtn).not.toBeDisabled();
  });

  it('requires a secret once a non-None auth mode is selected', () => {
    renderForm();
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: 'my-server' } });
    fireEvent.change(screen.getByLabelText(/remote url/i), { target: { value: 'https://example.test/mcp' } });
    fireEvent.change(screen.getByLabelText(/auth/i), { target: { value: 'bearer' } });

    expect(screen.getByRole('button', { name: /add http mcp server/i })).toBeDisabled();

    fireEvent.change(screen.getByLabelText(/secret/i), { target: { value: 'sk-token' } });
    expect(screen.getByRole('button', { name: /add http mcp server/i })).not.toBeDisabled();
  });
});

describe('McpServerCreateForm — error mapping', () => {
  it('shows a mapped inline message on a 409 duplicate-name failure', async () => {
    mockCreate.mockRejectedValue(new ApiError(409, ''));
    renderForm();

    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: 'dup-server' } });
    fireEvent.change(screen.getByLabelText(/remote url/i), { target: { value: 'https://example.test/mcp' } });
    fireEvent.click(screen.getByRole('button', { name: /add http mcp server/i }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(/already exists/i),
    );
  });

  it('surfaces the server\'s { error } message on a 400 failure (e.g. SSRF-rejected URL)', async () => {
    mockCreate.mockRejectedValue(new ApiError(400, JSON.stringify({ error: 'remoteUrl: private address blocked' })));
    renderForm();

    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: 'ssrf-server' } });
    fireEvent.change(screen.getByLabelText(/remote url/i), { target: { value: 'http://169.254.169.254/' } });
    fireEvent.click(screen.getByRole('button', { name: /add http mcp server/i }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('remoteUrl: private address blocked'),
    );
  });
});

describe('McpServerCreateForm — oauth mode', () => {
  it('hides header-name and secret fields for oauth (no static secret)', () => {
    renderForm();
    fireEvent.change(screen.getByLabelText(/auth/i), { target: { value: 'oauth' } });
    expect(screen.queryByLabelText(/header name/i)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/secret/i)).not.toBeInTheDocument();
  });

  it('on success with an authorizeUrl, redirects the browser to it', () => {
    const originalLocation = window.location;
    // @ts-expect-error -- test-only reassignment of a readonly global
    delete window.location;
    // @ts-expect-error -- test-only stub
    window.location = { ...originalLocation, href: '' };

    mockCreate.mockImplementation(() => Promise.resolve({
      id: 'srv-1', displayName: 'my-oauth', transport: 'http_cloud', remoteUrl: 'https://mcp.example.test/',
      authMode: 'oauth', authHeaderName: null, hasSecret: false, secretHint: null, status: 'NeedsReauth',
      connect: { authorizeUrl: 'https://as.example.test/authorize?state=xyz', error: null },
    }));
    renderForm();
    fireEvent.change(screen.getByLabelText(/name/i), { target: { value: 'my-oauth' } });
    fireEvent.change(screen.getByLabelText(/remote url/i), { target: { value: 'https://mcp.example.test/' } });
    fireEvent.change(screen.getByLabelText(/auth/i), { target: { value: 'oauth' } });
    fireEvent.click(screen.getByRole('button', { name: /add http mcp server/i }));

    return waitFor(() => {
      expect(window.location.href).toBe('https://as.example.test/authorize?state=xyz');
    }).finally(() => {
      // @ts-expect-error -- restore
      window.location = originalLocation;
    });
  });

  it('on success with a connect.error, shows an inline message but still calls onCreated (the row exists in NeedsReauth)', async () => {
    const onCreated = vi.fn();
    mockCreate.mockResolvedValue({
      id: 'srv-2', displayName: 'my-oauth-2', transport: 'http_cloud', remoteUrl: 'https://mcp.example.test/',
      authMode: 'oauth', authHeaderName: null, hasSecret: false, secretHint: null, status: 'NeedsReauth',
      connect: { authorizeUrl: null, error: 'This authorization server does not support dynamic client registration.' },
    });
    renderForm(onCreated);
    fireEvent.change(screen.getByLabelText(/name/i), { target: { value: 'my-oauth-2' } });
    fireEvent.change(screen.getByLabelText(/remote url/i), { target: { value: 'https://mcp.example.test/' } });
    fireEvent.change(screen.getByLabelText(/auth/i), { target: { value: 'oauth' } });
    fireEvent.click(screen.getByRole('button', { name: /add http mcp server/i }));

    await waitFor(() => expect(onCreated).toHaveBeenCalledWith('srv-2'));
  });
});

describe('McpServerCreateForm — auto-detect auth mode (onBlur)', () => {
  it('blurring the Remote URL field calls detectAuth and selects the returned mode (oauth)', async () => {
    mockDetectAuth.mockResolvedValue({ authMode: 'oauth' });
    renderForm();

    fireEvent.change(screen.getByLabelText(/remote url/i), {
      target: { value: 'https://mcp.example.test/mcp' },
    });
    fireEvent.blur(screen.getByLabelText(/remote url/i));

    await waitFor(() => expect(mockDetectAuth).toHaveBeenCalledWith('https://mcp.example.test/mcp'));
    await waitFor(() =>
      expect((screen.getByLabelText(/auth/i) as HTMLSelectElement).value).toBe('oauth'),
    );
    // oauth has no static secret — the field-visibility guard should reflect the auto-selection.
    expect(screen.queryByLabelText(/secret/i)).not.toBeInTheDocument();
  });

  it('blurring the Remote URL field selects "none" when detectAuth resolves none', async () => {
    mockDetectAuth.mockResolvedValue({ authMode: 'none' });
    renderForm();

    fireEvent.change(screen.getByLabelText(/auth/i), { target: { value: 'bearer' } });
    fireEvent.change(screen.getByLabelText(/remote url/i), {
      target: { value: 'https://mcp.example.test/mcp' },
    });
    fireEvent.blur(screen.getByLabelText(/remote url/i));

    await waitFor(() =>
      expect((screen.getByLabelText(/auth/i) as HTMLSelectElement).value).toBe('none'),
    );
  });

  it('an "unknown" result leaves the current auth selection untouched and shows a manual-pick hint', async () => {
    mockDetectAuth.mockResolvedValue({ authMode: 'unknown' });
    renderForm();

    fireEvent.change(screen.getByLabelText(/remote url/i), {
      target: { value: 'https://mcp.example.test/mcp' },
    });
    fireEvent.blur(screen.getByLabelText(/remote url/i));

    await waitFor(() => expect(mockDetectAuth).toHaveBeenCalledTimes(1));
    // untouched — still the default 'none'
    expect((screen.getByLabelText(/auth/i) as HTMLSelectElement).value).toBe('none');
    await waitFor(() => expect(screen.getByText(/couldn't detect/i)).toBeInTheDocument());
  });

  it('a rejected detectAuth call also leaves the selection untouched and shows the manual-pick hint', async () => {
    mockDetectAuth.mockRejectedValue(new Error('network error'));
    renderForm();

    fireEvent.change(screen.getByLabelText(/remote url/i), {
      target: { value: 'https://mcp.example.test/mcp' },
    });
    fireEvent.blur(screen.getByLabelText(/remote url/i));

    await waitFor(() => expect(screen.getByText(/couldn't detect/i)).toBeInTheDocument());
    expect((screen.getByLabelText(/auth/i) as HTMLSelectElement).value).toBe('none');
  });

  it('re-blurring the same URL does not call detectAuth again', async () => {
    mockDetectAuth.mockResolvedValue({ authMode: 'oauth' });
    renderForm();
    const urlInput = screen.getByLabelText(/remote url/i);

    fireEvent.change(urlInput, { target: { value: 'https://mcp.example.test/mcp' } });
    fireEvent.blur(urlInput);
    await waitFor(() => expect(mockDetectAuth).toHaveBeenCalledTimes(1));

    // Blur again with no change in value — must not re-probe.
    fireEvent.blur(urlInput);
    await new Promise((r) => setTimeout(r, 0));
    expect(mockDetectAuth).toHaveBeenCalledTimes(1);
  });

  it('does not probe a non-https-looking value', async () => {
    renderForm();
    fireEvent.change(screen.getByLabelText(/remote url/i), { target: { value: 'not-a-url' } });
    fireEvent.blur(screen.getByLabelText(/remote url/i));

    await new Promise((r) => setTimeout(r, 0));
    expect(mockDetectAuth).not.toHaveBeenCalled();
  });
});
