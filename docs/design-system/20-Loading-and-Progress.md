# 20 — Loading and Progress

## Selection

- Under 300 ms: no indicator.
- 300 ms–2 s: local spinner or skeleton.
- Over 2 s: text explaining the operation.
- Known multi-step work: determinate progress with step/value.
- Background work: nonblocking status with notification on completion.

Skeletons match the final structure and are used for initial content—not button submissions. Spinners remain local to the affected control/region. Buttons retain width and replace label content with an indicator while disabled.

Never clear existing data during refresh; show a subtle refreshing state. Announce loading and completion through `aria-live` without excessive repetition. A timeout becomes an actionable error, not an infinite spinner. Progress values are real; never simulate 90% indefinitely.
