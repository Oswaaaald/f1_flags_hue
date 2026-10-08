export class ApiFailure extends Error {
  constructor(
    message: string,
    readonly code: string,
    readonly status: number,
  ) {
    super(message);
  }
}
let view = new AbortController();
export function changeView() {
  view.abort();
  view = new AbortController();
}
export function viewApi<T>(path: string) {
  return api<T>(path, "GET", undefined, { signal: view.signal });
}

export async function api<T = unknown>(
  path: string,
  method = "GET",
  body?: unknown,
  options: { signal?: AbortSignal; revision?: number; timeoutMs?: number } = {},
): Promise<T> {
  const controller = new AbortController();
  const abort = () => controller.abort(options.signal?.reason);
  options.signal?.addEventListener("abort", abort, { once: true });
  if (options.signal?.aborted) abort();
  let timedOut = false;
  const timer = window.setTimeout(
    () => {
      timedOut = true;
      controller.abort();
    },
    options.timeoutMs ?? (method === "GET" ? 15000 : 45000),
  );
  try {
    const response = await fetch("/api" + path, {
      method,
      signal: controller.signal,
      headers: {
        "X-F1Hue-Request": "1",
        ...(body === undefined ? {} : { "Content-Type": "application/json" }),
        ...(options.revision === undefined
          ? {}
          : { "If-Match": `"${options.revision}"` }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const result = (await response
      .json()
      .catch(() => ({ error: "Réponse du service indisponible." }))) as T & {
      error?: string;
      code?: string;
    };
    if (!response.ok) {
      if (response.status === 401 && !path.startsWith("/auth"))
        window.dispatchEvent(new Event("f1hue:unauthorized"));
      throw new ApiFailure(
        result.error ?? `Erreur ${response.status}`,
        result.code ?? `http_${response.status}`,
        response.status,
      );
    }
    return result;
  } catch (error) {
    if (timedOut)
      throw new ApiFailure(
        method === "GET"
          ? "Le service met trop de temps à répondre. Vérifie sa connexion."
          : "Le service n’a pas confirmé le résultat. Vérifie son état avant de relancer cette action.",
        "timeout",
        0,
      );
    throw error;
  } finally {
    clearTimeout(timer);
    options.signal?.removeEventListener("abort", abort);
  }
}
