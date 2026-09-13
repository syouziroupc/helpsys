# Generic UI resolution hardening

This document tracks the generic guidance hardening work after the Google account chooser fixes.

Principles:
- resolve visible user choices locally whenever the clarification is explicitly asking the user to choose a visible item;
- never guess between multiple matching controls;
- keep raw clarification answers local when a visible choice can be resolved locally;
- prefer the current foreground HWND as the structural scope when available, with the existing process scan as a fallback;
- revalidate every chosen target immediately before presenting guidance;
- use current-state replan for transient screen movement rather than classifying ordinary UI movement as route failure.
