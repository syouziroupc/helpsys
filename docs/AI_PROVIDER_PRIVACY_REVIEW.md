# HelpSys AI provider privacy review

SAFE_RELEASE_STATUS: BLOCKED
SAFE_BINARY_PREVIEW_STATUS: ALLOWED_WITH_DEFAULT_EGRESS_OFF
REVIEW_DATE: 2026-09-12
PROVIDER: Cloudflare Workers AI
INTEGRATION: Workers AI binding (`env.AI.run`)

## Release rule

`SAFE_RELEASE_STATUS` describes whether HelpSys Safe may be represented as an approved **provider-connected** Safe deployment. It remains `BLOCKED` until every mandatory provider item below is `CONFIRMED`.

A HelpSys Safe Preview binary may still be distributed while this review is blocked only because the production Safe build has **no default cloud endpoint**. Without an explicitly configured reviewed Safe endpoint, it fails closed before a screenshot is created for cloud use and refuses external AI transmission. Cloud voice/Commander transcription is also disabled in the Safe build.

The binary-preview exception is valid only while all of the following remain true and are contract-tested:

- `HELPSYS_SAFE_BUILD` is compile-time fixed and cannot silently fall back to the normal profile.
- Safe has no production cloud default; an absent `HELPSYS_SAFE_API_BASE` means no cloud egress.
- Screen-derived cloud requests cannot bypass `PrivacyGate`.
- Password / OTP / token / API-key / Cookie/Storage / financial-auth states fail closed locally.
- Redirects and cookies are disabled for Safe transport, OS/user proxy inheritance is disabled, and only the code-reviewed HelpSys Workers origin is accepted if a Safe endpoint is explicitly configured.
- Safe cloud voice and Commander transcription remain disabled.

Changing any of those conditions requires a new review before continuing public Safe Preview distribution.

## Mandatory provider review

| Requirement | Status | Evidence / decision |
| --- | --- | --- |
| Customer content used for model training | CONFIRMED | Cloudflare states Workers AI Customer Content is not used to train Workers AI models without explicit consent. |
| Customer content used to improve Cloudflare or third-party services | CONFIRMED | Cloudflare states it is not used to improve Cloudflare or third-party services without explicit consent. |
| Application-controlled persistent AI storage | CONFIRMED | Current production Wrangler config has only static assets, Workers AI binding and variables; it has no R2, KV, Durable Objects, D1 or Vectorize binding. HelpSys must not add such a binding for screen-derived content without a new review. |
| Model-side output storage / distillation | CONFIRMED | HelpSys forces `store: false` for text/vision Workers AI calls and contract-tests that boundary. This does not by itself disable infrastructure-level prompt/prefix caching. |
| Workers AI prompt / prefix caching | UNRESOLVED | Cloudflare documents that prefix caching is enabled by default for select models and stores computed input tensors from the prefill stage. The current GLM-5.3-Flash model page exposes cached-input pricing, showing that cached input is part of that product path. Before approving provider-connected Safe operation, retain authoritative evidence for cache applicability, retention/lifetime, isolation and an enforceable disable/bypass control for every production model carrying Safe screen-derived content. `store: false` must not be treated as proof that prefix caching is disabled. |
| AI Gateway prompt/response logging | CONFIRMED_NOT_USED | Current HelpSys Worker invokes Workers AI through the `AI` binding. The current config does not route inference through an AI Gateway endpoint. If AI Gateway is introduced, raw payload logging must be explicitly disabled and reviewed before Safe release approval. |
| Processing geography / data residency of Workers AI inference | UNRESOLVED | Cloudflare documents Workers regionalization for Worker execution, but also states Regional Services does not extend to outgoing subrequests to other services. Workers AI is a separate GPU inference service on Cloudflare's global network. The reviewed public documentation does not establish an enforceable HelpSys requirement for a specific Workers AI inference processing region. |
| Applicable model / third-party license terms | UNRESOLVED | Cloudflare identifies Workers AI models as Third-Party Services and directs customers to review applicable model licenses. HelpSys currently uses Z.ai GLM-4.7-Flash, Z.ai GLM-5.3-Flash and OpenAI Whisper large-v3-turbo. A release owner must retain the applicable license/contract evidence for each production model before provider-connected Safe operation is approved. |
| Contract governing Workers AI processing | CONFIRMED | Cloudflare states Workers AI customer data is processed subject to its Privacy Policy and the applicable Self-Serve or Enterprise Subscription Agreement. Commercial deployment should retain the agreement applicable to the HelpSys account. |

## Current architecture evidence

Production `wrangler.jsonc` contains an `AI` binding and no persistent data-store binding. The multimodal Worker calls `env.AI.run()` directly.

The production desktop Safe build does not default to that Worker. `CloudAiAdapter` resolves Safe cloud configuration only from an explicit Safe endpoint and otherwise remains unconfigured. In that default distributed state, `CloudGuideService.PreflightPrivacy()` rejects cloud use before a screenshot is prepared for transmission, and Safe startup discloses that screen transmission is stopped. Safe cloud speech/Commander is disabled.

If a reviewed Safe endpoint is explicitly configured, the client restricts it to the code-reviewed HelpSys Workers origin, rejects redirects, disables cookies and OS/user proxy inheritance, and still requires Privacy Gate approval for screen-derived requests. These controls reduce accidental exfiltration paths but do not resolve the provider-side items above. Therefore `SAFE_RELEASE_STATUS` remains `BLOCKED` even while the local-egress-off Safe Preview binary may be distributed.

## Sources reviewed

- Cloudflare. (2026, April 21). *Data usage*. Cloudflare Workers AI documentation. https://developers.cloudflare.com/workers-ai/platform/data-usage/
- Cloudflare. (2026, April 21). *Prompt caching*. Cloudflare Workers AI documentation. https://developers.cloudflare.com/workers-ai/features/prompt-caching/
- Cloudflare. (2026). *glm-5.3-flash*. Cloudflare Workers AI model catalog. https://developers.cloudflare.com/workers-ai/models/glm-5.3-flash/
- Cloudflare. (2026, July 23). *Workers: Data Localization Suite*. Cloudflare documentation. https://developers.cloudflare.com/data-localization/how-to/workers/
- Cloudflare. (2026, April 21). *Workers Bindings*. Cloudflare Workers AI documentation. https://developers.cloudflare.com/workers-ai/configuration/bindings/
- Cloudflare. (2026, June 15). *Logging*. Cloudflare AI Gateway documentation. https://developers.cloudflare.com/ai-gateway/observability/logging/
- Cloudflare. (2026). *glm-4.7-flash*. Cloudflare Workers AI model catalog. https://developers.cloudflare.com/workers-ai/models/glm-4.7-flash/
- Cloudflare. (2026). *whisper-large-v3-turbo*. Cloudflare Workers AI model catalog. https://developers.cloudflare.com/workers-ai/models/whisper-large-v3-turbo/
