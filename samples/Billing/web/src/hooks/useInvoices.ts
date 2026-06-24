import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { rpc } from "@/lib/rpc"

export function useInvoices() {
  return useQuery({
    queryKey: ["invoices"],
    queryFn: ({ signal }) => rpc.invoices.list({}, { signal }),
  })
}

export function useCreateInvoice() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: {
      clientId: string
      amountCents: number
      currency: string
      dueDate: string
    }) => rpc.invoices.create(input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["invoices"] }),
  })
}

const SETTLED = new Set(["Succeeded", "Failed", "Cancelled"])

export function useSendStatus(jobId: string | undefined) {
  return useQuery({
    queryKey: ["send-status", jobId],
    enabled: !!jobId,
    queryFn: ({ signal }) => rpc.invoices.sendStatus({ jobId: jobId! }, { signal }),
    // Poll while the job is still working; stop once it settles.
    refetchInterval: (query) =>
      query.state.data && SETTLED.has(query.state.data.status) ? false : 1500,
  })
}
