import axios, { type AxiosInstance } from "axios";

export const baseUrl = import.meta.env.DEV ? "http://127.0.0.1:5124" : "";

export const baseApiUrl = `${baseUrl}/api`;

export const apiClient: AxiosInstance = axios.create({
  baseURL: baseApiUrl,
  timeout: 30000,
  withCredentials: true,
  paramsSerializer: {
    indexes: null,
  },
  headers: {
    "Content-Type": "application/json",
  },
});

// Read once before rendering; credentials never become query parameters.
export async function initializeSession() {
  const fragment = new URLSearchParams(location.hash.slice(1));
  const token = fragment.get("bootstrap");
  if (token) history.replaceState(null, "", location.pathname + location.search);
  const response = token
    ? await apiClient.post<{ csrf: string }>("/auth/session", { token })
    : await apiClient.get<{ csrf: string }>("/auth/csrf");
  apiClient.defaults.headers.common["X-STT-CSRF"] = response.data.csrf;
}
apiClient.interceptors.response.use(r => r, error => {
  if (error.response?.status === 401) {
    document.getElementById("session-error")?.remove();
    const message = document.createElement("div");
    message.id = "session-error";
    message.textContent = "Session expired. Open the interface again from the desktop or tray. 会话已过期，请从桌面或托盘重新打开。";
    document.body.prepend(message);
  }
  return Promise.reject(error);
});
