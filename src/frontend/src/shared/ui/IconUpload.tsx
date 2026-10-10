import { useState } from "react";
import { Button, Stack, Typography } from "@mui/material";
import { apiClient } from "@/shared/api";
export function IconUpload({ value = "", name, onValueChange }: { value?: string; name?: string; onValueChange?: (value: string) => Promise<void> }) {
  const [selected, setSelected] = useState(value);
  const [error, setError] = useState("");
  const update = async (id: string) => { await onValueChange?.(id); setSelected(id); setError(""); };
  return <Stack direction="row" spacing={1}>
    {name && <input type="hidden" name={name} value={selected} />}
    <Button component="label" size="small">Upload / 上传<input hidden type="file" accept="image/png,image/jpeg,image/x-icon,.ico" onChange={async e => {
      const file = e.target.files?.[0]; e.target.value = "";
      if (!file) return;
      if (file.size > 2 * 1024 * 1024) { setError("Maximum 2 MiB"); return; }
      try { const r = await apiClient.post("/icons", file, { headers: { "Content-Type": "application/octet-stream" } }); await update(r.data.iconId); }
      catch { setError("Invalid image or upload failed / 图片无效或上传失败"); }
    }} /></Button>
    <Button size="small" onClick={async () => { try { await update(""); } catch { setError("Unable to clear icon / 删除失败"); } }}>Clear / 删除</Button>
    {error && <Typography role="alert">{error}</Typography>}
  </Stack>;
}
