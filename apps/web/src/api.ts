export async function api<T = unknown>(
  path: string,
  method = "GET",
  body?: unknown,
): Promise<T> {
  const response = await fetch("/api" + path, {
    method,
    headers: {
      "X-F1Hue-Request": "1",
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const result = (await response
    .json()
    .catch(() => ({ error: "Réponse du service indisponible." }))) as T & {
    error?: string;
  };
  if (!response.ok) {
    if (response.status === 401 && !path.startsWith("/auth"))
      window.dispatchEvent(new Event("f1hue:unauthorized"));
    throw new Error(result.error ?? `Erreur ${response.status}`);
  }
  return result;
}
