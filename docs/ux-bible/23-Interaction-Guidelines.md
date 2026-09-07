# 23 — Interaction Guidelines

Interaction states are required: default, hover where available, visible focus, pressed/selected, disabled with reason where useful, loading, success and error. Hover never carries unique functionality. Pressed state is immediate; loading prevents duplicate activation without erasing the label.

Primary actions use Metronic/Bootstrap button patterns and one page-level primary. Enter submits only where unambiguous; Escape closes the top dismissible layer; destructive shortcuts are prohibited. Async updates announce results and retain context. Optimistic updates are limited to reversible, low-risk actions.

Selection and navigation are distinct: checkboxes select; row/link opens. Whole-row activation must preserve text selection and nested controls. Drag-and-drop always has menu/keyboard alternatives. Inline edit saves explicitly for consequential data and communicates dirty state.

Touch targets are 44px (36px compact desktop only); pointer cursor does not make nonsemantic elements interactive. RTL reverses spatial semantics, not media playback or numeric meaning. Dark mode changes tokens, not state meaning.
