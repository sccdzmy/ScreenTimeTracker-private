import { useState } from "react";
import { apiClient } from "@/shared/api";
import { Button, Stack, Typography } from "@mui/material";
export function ExtensionPairing() {
  const [code, setCode] = useState("");
  const [error, setError] = useState("");
  return <Stack spacing={1} sx={{ p: 2 }}>
    <Typography>Browser extension / 浏览器扩展</Typography>
    <Button onClick={async () => { try { const r = await apiClient.post("/auth/pairing-code", {}); setCode(r.data.code); setError(""); } catch { setError("Unable to create pairing code / 无法生成配对码"); } }}>Generate pairing code / 生成配对码</Button>
    {code && <Typography>Paste into the extension settings within 5 minutes / 五分钟内填写到扩展设置：{code}</Typography>}
    <Button onClick={async () => { try { await apiClient.delete("/auth/extensions"); setCode(""); setError("Extensions revoked / 已撤销扩展"); } catch { setError("Revocation failed / 撤销失败"); } }}>Revoke all extensions / 撤销全部扩展</Button>
    <Typography role="status">{error}</Typography>
  </Stack>;
}
