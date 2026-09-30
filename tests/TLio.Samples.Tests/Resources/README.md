# Resources

`actus-tests-pam.json` — the 42 ACTUS PAM reference cases (`pam01`..`pam42`): contract terms,
observed market data and the expected event list (`eventDate`, `eventType`, `payoff`,
`notionalPrincipal`, `nominalInterestRate`, `accruedInterest`).

Copied unchanged from ACTUS-I (`Actus-Insurance.GPU/TestData/actus-tests-pam.json`), which is
the file its own PAM tests and benchmarks use, so both engines are held to the same reference.
The cases originate from the ACTUS test suite (actusfrf). `ActusPamReferenceTests` runs them
against the Tlio PAM scripts.
