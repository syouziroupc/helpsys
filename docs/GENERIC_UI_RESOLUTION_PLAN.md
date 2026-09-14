# Generic UI resolution hardening

This work generalizes the account-chooser fixes into product-wide UI invariants instead of adding site-specific branches.

Implemented invariants:
- prefer candidates geometrically bound to the current foreground HWND when that HWND belongs to the requested process;
- retain the established process-wide scanner only as a compatibility fallback when foreground scoping is unavailable or produces no actionable element;
- preserve Windows shell cross-process behavior for Explorer, SearchHost and StartMenuExperienceHost;
- treat visible user-choice clarification as a generic local operation, not an account/browser special case;
- resolve a visible choice only when one clickable target is uniquely supported by its own label or a geometrically contained visible child label;
- never use nearest-neighbour guessing for a user choice;
- keep the raw visible-choice answer local and store only an opaque history label;
- revalidate every resolved target immediately before presenting guidance and reject targets outside the current foreground window when Windows can bind that window reliably;
- use two bounded current-state observations for transient UI/observer uncertainty, with a longer settle delay on the second observation;
- never convert a technical observer failure into a user clarification answer;
- reserve heavy RouteRecovery for confirmed repeated action failure rather than ordinary UI movement or observation uncertainty.

Safety boundaries remain unchanged: Privacy Gate stays fail-closed for protected states, and HelpSys remains guidance-only rather than clicking or typing on the user's behalf.
