# Security Policy

## Supported versions

| Version | Supported |
| ------- | --------- |
| 2.x     | Yes       |
| 1.x     | No — upgrade to 2.x (1.0.0 bundles a vulnerable SQLite, GHSA-2m69-gcr7-jv3q) |

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub:
**Security → Report a vulnerability** on https://github.com/Nertti-Technologies/hermex
(private vulnerability reporting). Do not open a public issue.

Include the Hermex version, the .NET version, and the steps or code needed to reproduce the
problem. You will get an acknowledgement on the report, and we will keep you informed as it is
investigated and fixed.

## Deployment note

Hermex is a development tool. Its defaults assume a single developer on `localhost`. Before
exposing the dashboard to other people, read "Running Hermex for a team" in the README.
