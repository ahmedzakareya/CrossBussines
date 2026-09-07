# 33 — Loading States

Under ~300ms, avoid a spinner flash. From ~300ms to 2s, use local skeleton/spinner. For measurable work over 2s, show determinate progress and task label. For long server work, allow background continuation, status lookup and safe exit.

Skeletons match final geometry, use no fake data, and are `aria-hidden`; a nearby status announces loading. Buttons retain their label plus spinner and reject duplicate activation. Load panels independently; never block the whole shell for one widget. Preserve previous content during refresh when safe and label it updating/stale.

Cancellation is available for expensive searches/reports. Timeout becomes an error/unavailable state with retry. Reduced motion removes shimmer in favor of static placeholders. No infinite spinner, global overlay for local work, or layout shift on completion.
