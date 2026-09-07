# 28 — Table Patterns

Use Metronic/Bootstrap table markup inside `.table-responsive`; DataTables only when its capability is actually required. A table is for comparing structured records, not arbitrary layout.

Anatomy: caption/accessible name, toolbar, filters, column headers, body, optional selection/action column, summary and pagination. Left-align text logically; align numbers consistently with tabular figures. Sticky headers/actions require overlap and keyboard testing. Row actions use a KeenIcon-labeled menu; selection never equals navigation.

Sort state is announced; server paging/filtering is preferred for large data. Loading uses fixed row skeletons; empty spans columns and reflects filters; error preserves filters; denied never renders a blank table. On narrow screens use priority columns/cards, or horizontal scroll where comparison is essential with an overflow cue. RTL reverses logical columns but keeps amounts/codes readable. Avoid more than one nested table and avoid action-icon clusters.
