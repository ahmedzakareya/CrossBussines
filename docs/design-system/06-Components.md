# 06 — Components

## Component contract

Every reusable component defines anatomy, variants, states, responsive behavior, RTL, keyboard behavior, accessibility name, localization, authorization boundary, and data-state behavior.

## Core families

- Actions: primary, secondary, quiet, icon, destructive, split/menu buttons.
- Containers: card, panel, widget, section, drawer, modal, popover.
- Data: table, tree, timeline, kanban, calendar, chart, report viewer.
- Input: text, number, amount, date/time, Select2 lookup, Tagify search, checkbox/radio/switch, upload.
- Feedback: status chip, badge, alert, toast, progress, spinner, skeleton, empty/unavailable/denied/error states.
- Collaboration: comment, mention, reaction, participant, attachment, chat message.

## General rules

Use Metronic/Bootstrap markup where it already satisfies the contract. Add a CrossBusiness wrapper only when it encodes repeated business behavior or state. Prefer partials for static markup today and compiled ViewComponents when behavior, authorization, or testing warrants it. Full inventory is in `32-Component-Catalog.md`.
