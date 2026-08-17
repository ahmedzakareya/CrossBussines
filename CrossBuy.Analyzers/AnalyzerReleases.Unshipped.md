; Analyzer release tracking. Roslyn's RS2008 requires every rule to be declared here so that adding, removing or
; re-severing a rule is a visible diff rather than an invisible behaviour change — the same discipline as the
; shrink-only authorization baseline itself.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CBA001 | CrossBuy.Authorization | Warning | New unprotected mutating endpoint, absent from the frozen baseline
CBA002 | CrossBuy.Authorization | Warning | Authentication, anti-forgery or lane guard present; authorization absent
CBA003 | CrossBuy.Authorization | Warning | Authorization-shaped call resolves to no declared authority
CBA004 | CrossBuy.Authorization | Warning | Baseline entry no longer matches an unprotected mutating endpoint
CBA005 | CrossBuy.Authorization | Warning | Anonymous mutating endpoint not declared AnonymousByDesign
CBA006 | CrossBuy.Authorization | Warning | An authorization diagnostic is suppressed
