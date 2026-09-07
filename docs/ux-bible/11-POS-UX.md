# 11 — POS UX

POS is a specialized operational surface, not a dashboard theme. Evidence spans `Views/Pos`, `Views/PosApp`, `Views/Hyper`, `_LayoutPos.cshtml`, `_LayoutPosApp.cshtml`, `_LayoutHyperPos.cshtml`, and `wwwroot/Backend-assets/css/pos-cashier.css`.

Cashier flow is login/open shift → identify customer where needed → scan/search → review basket → discounts/authorization → payment → receipt → ready state. The sale total, basket, current input and payment state dominate. Secondary administration never competes with tendering.

Offline/degraded, price unavailable, item not found, age/manager approval, insufficient payment, printer failure, duplicate submission and successful sale are explicit states. Never clear a basket on ambiguous failure. Destructive void/refund names item/receipt and permission requirement. Touch targets are at least 44px; scanner and keyboard focus is deterministic; sound has visual equivalents.

Desktop/lane layouts remain dense; tablet supports touch; phone is limited to expressly supported PosApp flows. RTL mirrors composition while amounts/barcodes remain readable. Dark mode must retain tender/status contrast. Preload critical catalog data, debounce search, and keep perceived scan response immediate. Future evolution may unify tokens and shared feedback without replacing the operational shell.
