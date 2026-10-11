export const escapeHtml = (value: unknown): string => String(value).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);
export const safeColor = (value: unknown): string => typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value) ? value : "#888888";
