import {useMutation, useQuery, useQueryClient} from "@tanstack/react-query"
import {rpc} from "@/lib/rpc"

export function useClients() {
  return useQuery({
    queryKey: ["clients"],
    queryFn: ({signal}) => rpc.clients.list({}, {signal}),
    //         ^ AbortSignal forwarded to fetch — the request is cancelled on cleanup
  })
}

export function useCreateClient() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: { name: string; email: string }) => rpc.clients.create(input),
    onSuccess: () => queryClient.invalidateQueries({queryKey: ["clients"]}),
  })
}
