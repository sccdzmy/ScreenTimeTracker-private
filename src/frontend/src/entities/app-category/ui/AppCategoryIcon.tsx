import { getAppCategoryIconUrl } from "../api/requests";
import type { SxProps, Theme } from "@mui/material/styles";
import Box from "@mui/material/Box";
import UnknownCategory from "@/shared/ui/UnknownCategory.svg";

export const AppCategoryIcon = ({
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
      src={iconId === null ? UnknownCategory : getAppCategoryIconUrl(id, iconPathLastUpdatedAt)}
      className={className}
      sx={[{}, ...(Array.isArray(sx) ? sx : [sx])]}
    />
  );
};
