import { getAppIconUrl } from "../api/requests";
import type { SxProps, Theme } from "@mui/material/styles";
import Box from "@mui/material/Box";
import UnknownApp from "@/shared/ui/UnknownApp.svg";

export const AppIcon = ({
  sx,
  className,
  id,
  iconId,
  iconPathLastUpdatedAt,
}: {
  sx?: SxProps<Theme>;
  className?: string;
  id: string;
  iconId: string | null;
  iconPathLastUpdatedAt: Date;
}) => {
  return (
    <Box
      component="img"
      src={
        iconId === null
          ? UnknownApp
          : getAppIconUrl(id, iconPathLastUpdatedAt)
      }
      className={className}
      sx={[{}, ...(Array.isArray(sx) ? sx : [sx])]}
    />
  );
};
