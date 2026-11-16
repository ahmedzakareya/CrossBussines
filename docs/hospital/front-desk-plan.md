# Hospital front desk — the gap and the plan

Owner, 2026-11-16: «لا نملك شاشة استقبال محترمة مجمع بها كل شيء» ("we have no proper reception screen that brings everything together").

## The gap

Reception today means eleven separate menu entries, each a screen of its own:

- patient registry, register patient, self-intake, unidentified patients, duplicate files;
- births and deaths, consent forms;
- appointments, portal requests, earlier-slot waitlist, patient SMS.

A receptionist hops between them for one patient. Nothing shows the desk's day at a glance: who is expected, who has arrived, who is waiting and for how long, who still owes money.

## What one front-desk workspace must hold

The leading HIS front desks (Epic Cadence/Prelude, Cerner registration, TrakCare front office) are built around one patient search and one day board.

1. **One search box:** name in Arabic and English (with the H-series Arabic name folding), MRN, phone or national ID. It finds the patient, or offers to register one with duplicate warnings inline.
2. **Today's board:** every appointment by clinic and doctor, and its state (expected → arrived → with the doctor → done / no-show). It includes walk-ins, the queue number and the waiting time.
3. **Patient card:** the result of the search or a click on the board. It shows:
   - photo, identifiers, insurance and its validity, allergies, outstanding balance;
   - upcoming and past visits;
   - consents due, and missing documents (ID, insurance card scan, uploaded to the File Manager).
4. **The desk's actions from the card, without leaving it:**
   - check in;
   - book or move an appointment;
   - take a deposit or a payment;
   - print the visit slip through Report Studio;
   - send the patient SMS;
   - start an admission.
5. **Inbox:** portal requests, waitlist offers and self-intake forms waiting for a receptionist, each with its age.
6. **Figures:** expected today, arrived, no-shows, average wait, money collected.

## Boundaries

- Appointments, the queue screens and the kiosks belong to other sessions. The front desk **reads and links to** them through their services; it does not edit their views.
- Patient search reuses the registry's search and the Arabic name folding. There is no second search.

## When

Scheduled right after the pharmacy's ward-cabinet unit (H138). It is to be designed from the demo39 dashboard and app layouts, prototype first, then built.
