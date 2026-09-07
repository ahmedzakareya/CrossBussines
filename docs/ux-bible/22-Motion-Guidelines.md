# 22 — Motion Guidelines

Motion explains change, preserves spatial continuity, and confirms feedback. It never delays work.

| Interaction | Duration | Easing/behavior |
|---|---:|---|
| Hover/focus color | 100–150ms | standard ease-out |
| Press feedback | 80–120ms | immediate scale/color, no layout shift |
| Dialog enter/exit | 180/140ms | fade + subtle scale; focus after enter |
| Drawer enter/exit | 220/180ms | directional slide using logical edge |
| Toast enter/exit | 180/140ms | fade/short translate; no focus steal |
| Page/content change | 120–180ms | content fade only; shell remains stable |
| Skeleton → content | 150ms | cross-fade without geometry shift |
| Timeline insertion | 160ms | brief highlight, no auto-scroll unless initiated |
| Workspace widget refresh | 150ms | local cross-fade, never whole-page flash |
| Dashboard chart | 300–500ms | initial reveal only; updates ≤250ms |
| Loading progress | continuous | determinate when measurable; no decorative loop |

Use Metronic/Bootstrap transitions where supplied. `prefers-reduced-motion: reduce` removes transforms, parallax, auto-scrolling, chart drawing and nonessential transitions; retain instant state change and progress text. Toast duration is content-dependent: default 5s, persistent for actionable/error messages. Pausing/hovering a timed notice pauses dismissal. Never animate large layout reflow, financial totals as decoration, or every dashboard widget simultaneously.
