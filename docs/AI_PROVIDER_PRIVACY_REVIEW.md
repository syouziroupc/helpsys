# HelpSys AI provider privacy review

SAFE_RELEASE_STATUS: BLOCKED
REVIEW_DATE: 2026-09-11
PROVIDER: Cloudflare Workers AI
INTEGRATION: Workers AI binding (`env.AI.run`)

## Release rule

HelpSys Safe must not be publicly released unless every mandatory item below is `CONFIRMED` and `SAFE_RELEASE_STATUS` is changed to `APPROVED` in a reviewed commit.

## Mandatory review

| Requirement | Status | Evidence / decision |
| --- | --- | --- |
| Customer content used for model training | CONFIRMED | Cloudflare states Workers AI Customer Content is not used to train Workers AI models without explicit consent. |
| Customer content used to improve Cloudflare or third-party services | CONFIRMED | Cloudflare states it is not used to improve Cloudflare or third-party services without explicit consent. |
| Application-controlled persistent AI storage | CONFIRMED | Current production Wrangler config has only static assets, Workers AI binding and variables; it has no R2, KV, Durable Objects, D1 or Vectorize binding. HelpSys must not add such a binding for screen-derived content without a new review. |
| Model-side output storage / distillation | CONFIRMED | HelpSys forces `store: false` for text/vision Workers AI calls and contract-tests that boundary. This does not by itself disable infrastructure-level prompt/prefix caching. |
| Workers AI prompt / prefix caching | UNRESOLVED | Cloudflare documents that prefix caching is enabled by default for select models and stores computed input tensors from the prefill stage. The current GLM-5.3-Flash model page exposes cached-input pricing, showing that cached input is part of that product path. Before Safe release, retain authoritative evidence for cache applicability, retention/lifetime, isolation and an enforceable disable/bypass control for every production model carrying Safe screen-derived content. `store: false` must not be treated as proof that prefix caching is disabled. |
| AI Gateway prompt/response logging | CONFIRMED_NOT_USED | Current HelpSys Worker invokes Workers AI through the `AI` binding. The current config does not route inference through an AI Gateway endpoint. If AI Gateway is introduced, raw payload logging must be explicitly disabled and reviewed before Safe release. |
| Processing geography / data residency of Workers AI inference | UNRESOLVED | Cloudflare documents Workers regionalization for Worker execution, but also states Regional Services does not extend to outgoing subrequests to other services. Workers AI is a separate GPU inference service on Cloudflare's global network. The reviewed public documentation does not establish an enforceable HelpSys requirement for a specific Workers AI inference processing region. |
| Applicable model / third-party license terms | UNRESOLVED | Cloudflare identifies Workers AI models as Third-Party Services and directs customers to review applicable model licenses. HelpSys currently uses Z.ai GLM-4.7-Flash, Z.ai GLM-5.3-Flash and OpenAI Whisper large-v3-turbo. A release owner must retain the applicable license/contract evidence for each production model. |
| Contract governing Workers AI processing | CONFIRMED | Cloudflare states Workers AI customer data is processed subject to its Privacy Policy and the applicable Self-Serve or Enterprise Subscription Agreement. Commercial deployment should retain the agreement applicable to the HelpSys account. |

## Current architecture evidence

Production `wrangler.jsonc` contains an `AI` binding and no persistent data-store binding. The multimodal Worker calls `env.AI.run()` directly. The Safe desktop build therefore remains blocked from public distribution because Workers AI prompt-cache handling, processing geography and model-license evidence are not yet fully resolved, not because a `store: false` flag alone is sufficient.

The desktop Safe build also fails closed unless its dedicated Safe endpoint is configured. Its transport now rejects automatic HTTP redirects, disables cookies and restricts external Safe egress to the code-reviewed HelpSys Workers origin. Those client controls reduce accidental exfiltration paths but do not replace the provider review above.

## Sources reviewed

- Cloudflare. (2026, April 21). *Data usage*. Cloudflare Workers AI documentation. https://developers.cloudflare.com/workers-ai/platform/data-usage/
- Cloudflare. (2026, April 21). *Prompt caching*. Cloudflare Workers AI documentation. https://developers.cloudflare.com/workers-ai/features/prompt-caching/
- Cloudflare. (2026). *glm-5.3-flash*. Cloudflare Workers AI model catalog. https://developers.cloudflare.com/workers-ai/models/glm-5.3-flash/
- Cloudflare. (2026, July 23). *Workers: Data Localization Suite*. Cloudflare documentation. https://developers.cloudflare.com/data-localization/how-to/workers/
- Cloudflare. (2026, April 21). *Workers Bindings*. Cloudflare Workers AI documentation. https://developers.cloudflare.com/workers-ai/configuration/bindings/
- Cloudflare. (2026, June 15). *Logging*. Cloudflare AI Gateway documentation. https://developers.cloudflare.com/ai-gateway/observability/logging/
- Cloudflare. (2026). *glm-4.7-flash*. Cloudflare Workers AI model catalog. https://developers.cloudflare.com/workers-ai/models/glm-4.7-flash/
- Cloudflare. (2026). *whisper-large-v3-turbo*. Cloudflare Workers AI model catalog. https://developers.cloudflare.com/workers-ai/models/whisper-large-v3-turbo/
