# 17 — Portal UX

Portal/public experiences include `Views/Portal`, `Views/Store`, `Views/Home/Store.cshtml`, authentication/account views, and print/embed entry points. They share tokens but not internal navigation or disclosure assumptions.

Visitors and limited external users need a clear identity, current relationship/context, focused task, help, and safe exit. Login/forms use Metronic/Bootstrap form primitives with accessible validation. Authenticated portal actions show organization, request/order status and next step without exposing internal identifiers or navigation.

States distinguish invalid input, expired link/session, unavailable service, denied resource, no records, pending review and success with reference. Do not reveal whether protected accounts/records exist. Responsive is mobile-first; RTL and localized content are first-class; dark mode follows declared support. Use progressive enhancement and small asset budgets. Future portal expansion requires a dedicated screen inventory and threat/privacy review.
