import { useMutation } from "@tanstack/react-query";
import { deleteUsageData, importData } from "./requests";
import type { DeleteUsageDataParams, ImportDataDto } from "./schemas";

export const useDeleteData = () => {
  return useMutation<void, Error, DeleteUsageDataParams>({
    mutationFn: deleteUsageData,
  });
};

export const useImportData = () => {
  return useMutation<ImportDataDto, Error, string>({
    mutationFn: importData,
  });
};
