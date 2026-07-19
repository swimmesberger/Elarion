import {createRpcApi} from "@/generated/rpc-client"

// The API base URL is injected by the Aspire app host (VITE_API_URL); falls back to localhost for a
// standalone `npm run dev`. No token resolver here: in Development the host stamps a dev principal, so
// the endpoints are callable without an issuer. In production you would pass
// `headers: () => ({ Authorization: \`Bearer ${getAccessToken()}\` })`.
const baseUrl = import.meta.env.VITE_API_URL ?? "http://localhost:5000"

export const rpc = createRpcApi({
  url: `${baseUrl.replace(/\/$/, "")}/rpc`,
})
