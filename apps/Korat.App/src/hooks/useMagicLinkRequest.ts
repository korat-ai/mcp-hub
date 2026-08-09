import { useMutation } from '@tanstack/react-query'
import { api } from '@/lib/api'

export function useMagicLinkRequest() {
  return useMutation({
    mutationFn: ({ email }: { email: string }) => api.auth.requestMagicLink(email),
  })
}
