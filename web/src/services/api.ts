interface RequestOptions { method?: string; body?: unknown; signal?: AbortSignal }
export async function request<T = unknown>(path: string, { method = "GET", body, signal }: RequestOptions = {}): Promise<T> {
  const options: RequestInit = { method, signal };
  if (body != null) {
    options.headers = { "Content-Type": "application/json" };
    options.body = JSON.stringify(body);
  }
  const response = await fetch("/api" + path, options);
  const result = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) throw Object.assign(new Error(result?.error || "操作失败，请检查工作台连接"), { status:response.status });
  return result;
}
