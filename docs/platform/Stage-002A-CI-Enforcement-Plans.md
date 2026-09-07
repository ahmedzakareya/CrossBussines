# Stage 2A — CI Enforcement Plans (ready to apply, not applied)

**Not applied.** No CI configuration exists in the repository and the platform decision is still open. Both pipelines
below are complete and ready to commit as-is once a platform is chosen — that is the precondition for leaving CI
pending while the Entry Gate is MET on local build enforcement.

**Local build enforcement is already active and proven** (see `Stage-002A-Entry-Gate-Final-Report.md`). CI adds
*remote* enforcement — it does not add the gate itself.

---

## 1. What CI must do, and the one trap

| Requirement | Why |
|---|---|
| Build the solution in **Release** | CBA001/004/006 are errors in every configuration; Release is the one CI would ship |
| Set **`CROSSBUY_SQL_REQUIRED=1`** | otherwise 72 SQL evidence tests skip and CI is green while proving nothing (Batch 00-C guard 4) |
| Provide **`CROSSBUY_TEST_SQL`** pointing at an **instance**, never a catalog | the fixture refuses a real CrossBuy catalog and the run fails loudly (guard 1) |
| Ensure `engineering/authorization-baseline.json` is in the checkout | absent, CBA001/CBA004 degrade to silent; the `CBA000` MSBuild guard turns that into a hard error |
| Run a **seeded-violation step** | see below |
| Publish the generated evidence artifacts | `Roslyn-Authorization-Inventory.csv`, `Roslyn-Analyzer-Diagnostic-Profile.csv`, `SQL-Evidence-Summary.md`, `Stage-002-Risk-Register.csv` |

### The trap, stated once more because it is the only way this can fail silently

A pipeline that builds **without** the analyzer wired, or without the baseline present, is **green and useless**. The
countermeasure is not documentation — it is the **seeded-violation step**: CI deliberately introduces a new
unprotected mutating endpoint, asserts the build FAILS with CBA001, then discards it. A pipeline that cannot fail on
purpose has not been shown to be able to fail at all.

Both pipelines below include that step as a permanent job, not a one-off.

## 2. GitHub Actions — `.github/workflows/entry-gate.yml`

```yaml
name: Stage 2A Entry Gate

on:
  push:
    branches: [main, master]
  pull_request:

jobs:
  entry-gate:
    # windows-latest, not ubuntu: the SQL evidence needs SQL Server, and the app is a Windows-hosted
    # ASP.NET Core application. A Linux runner would silently skip the SQL half of the gate.
    runs-on: windows-latest

    env:
      # Instance only. A catalog name here is refused by SqlEvidenceGuards and fails the run.
      CROSSBUY_TEST_SQL: "Server=(local)\\SQL2019;User ID=sa;Password=${{ secrets.SQL_SA_PASSWORD }};TrustServerCertificate=True;Connection Timeout=120;"
      # Makes an unconfigured or unreachable instance a FAILURE instead of 72 skips.
      CROSSBUY_SQL_REQUIRED: "1"

    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Start SQL Server
        run: |
          Set-Service -Name 'MSSQL$SQL2019' -StartupType Manual
          Start-Service -Name 'MSSQL$SQL2019'
        shell: pwsh

      # The baseline must exist or the analyzer enforces nothing. The csproj CBA000 guard also catches this,
      # but failing here names the cause without a build log.
      - name: Verify the authorization baseline is present
        run: |
          if (-not (Test-Path 'engineering/authorization-baseline.json')) {
            throw 'engineering/authorization-baseline.json is missing - the analyzer would enforce nothing.'
          }
        shell: pwsh

      - name: Restore
        run: dotnet restore CrossBuy.sln

      # CBA001, CBA004 and CBA006 are errors, so this step IS the authorization gate.
      - name: Build (Release) - authorization gate
        run: dotnet build CrossBuy.sln -c Release --no-restore

      - name: Analyzer tests
        run: dotnet test CrossBuy.Analyzers.Tests/CrossBuy.Analyzers.Tests.csproj -c Release --no-build

      - name: Application tests with SQL evidence
        run: dotnet test CrossBuy.Tests/CrossBuy.Tests.csproj -c Release --no-build

      # THE STEP THAT PROVES THE GATE IS REAL. Without it a misconfigured pipeline is green and enforces nothing.
      - name: Seeded violation must FAIL the build
        shell: pwsh
        run: |
          $probe = 'CrossBuy/Controllers/ZzCiSeededViolationController.cs'
          @'
          using Microsoft.AspNetCore.Mvc;
          namespace CrossBuy.Controllers
          {
              public class ZzCiSeededViolationController : Controller
              {
                  [HttpPost] public IActionResult SeededViolation() => Ok();
              }
          }
          '@ | Set-Content $probe -Encoding utf8
          try {
            dotnet build CrossBuy/CrossBuy.csproj -c Release 2>&1 | Tee-Object -Variable log | Out-Null
            if ($LASTEXITCODE -eq 0) {
              throw 'ENFORCEMENT IS NOT ACTIVE: a new unprotected mutating endpoint did not fail the build.'
            }
            if (($log -join "`n") -notmatch 'error CBA001') {
              throw "The build failed, but not with CBA001. The analyzer may not be loaded."
            }
            Write-Host 'Enforcement confirmed: CBA001 failed the build.'
          } finally {
            Remove-Item $probe -Force -ErrorAction SilentlyContinue
          }

      - name: Rebuild clean after the seeded violation
        run: dotnet build CrossBuy/CrossBuy.csproj -c Release

      - name: Publish evidence
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: stage-2a-evidence
          path: |
            docs/architecture/evidence/Roslyn-Authorization-Inventory.csv
            docs/architecture/evidence/Roslyn-Analyzer-Diagnostic-Profile.csv
            docs/architecture/evidence/Roslyn-Analyzer-Build-Impact.csv
            docs/architecture/evidence/SQL-Evidence-Summary.md
            docs/architecture/evidence/Stage-002-Risk-Register.csv
```

## 3. Azure DevOps — `azure-pipelines.yml`

```yaml
trigger:
  branches:
    include: [main, master]
pr:
  branches:
    include: ['*']

pool:
  vmImage: 'windows-2022'

variables:
  # Instance only — a catalog name is refused by SqlEvidenceGuards and fails the run.
  CROSSBUY_TEST_SQL: 'Server=(local)\SQL2019;User ID=sa;Password=$(SqlSaPassword);TrustServerCertificate=True;Connection Timeout=120;'
  CROSSBUY_SQL_REQUIRED: '1'

steps:
  - task: UseDotNet@2
    inputs:
      version: '10.0.x'

  - powershell: |
      Set-Service -Name 'MSSQL$SQL2019' -StartupType Manual
      Start-Service -Name 'MSSQL$SQL2019'
    displayName: 'Start SQL Server'

  - powershell: |
      if (-not (Test-Path 'engineering/authorization-baseline.json')) {
        throw 'engineering/authorization-baseline.json is missing - the analyzer would enforce nothing.'
      }
    displayName: 'Verify the authorization baseline is present'

  - script: dotnet restore CrossBuy.sln
    displayName: 'Restore'

  # CBA001, CBA004 and CBA006 are errors, so this step IS the authorization gate.
  - script: dotnet build CrossBuy.sln -c Release --no-restore
    displayName: 'Build (Release) - authorization gate'

  - script: dotnet test CrossBuy.Analyzers.Tests/CrossBuy.Analyzers.Tests.csproj -c Release --no-build
    displayName: 'Analyzer tests'

  - script: dotnet test CrossBuy.Tests/CrossBuy.Tests.csproj -c Release --no-build
    displayName: 'Application tests with SQL evidence'

  # THE STEP THAT PROVES THE GATE IS REAL.
  - powershell: |
      $probe = 'CrossBuy/Controllers/ZzCiSeededViolationController.cs'
      @'
      using Microsoft.AspNetCore.Mvc;
      namespace CrossBuy.Controllers
      {
          public class ZzCiSeededViolationController : Controller
          {
              [HttpPost] public IActionResult SeededViolation() => Ok();
          }
      }
      '@ | Set-Content $probe -Encoding utf8
      try {
        $log = dotnet build CrossBuy/CrossBuy.csproj -c Release 2>&1
        if ($LASTEXITCODE -eq 0) {
          throw 'ENFORCEMENT IS NOT ACTIVE: a new unprotected mutating endpoint did not fail the build.'
        }
        if (($log -join "`n") -notmatch 'error CBA001') {
          throw 'The build failed, but not with CBA001. The analyzer may not be loaded.'
        }
        Write-Host 'Enforcement confirmed: CBA001 failed the build.'
      } finally {
        Remove-Item $probe -Force -ErrorAction SilentlyContinue
      }
    displayName: 'Seeded violation must FAIL the build'

  - script: dotnet build CrossBuy/CrossBuy.csproj -c Release
    displayName: 'Rebuild clean after the seeded violation'

  - task: PublishBuildArtifacts@1
    condition: always()
    inputs:
      PathtoPublish: 'docs/architecture/evidence'
      ArtifactName: 'stage-2a-evidence'
```

## 4. Which to choose

No recommendation is made — this is the open platform decision. The two pipelines are deliberately step-for-step
equivalent so the choice is about hosting, not about what gets enforced.

**One shared prerequisite either way:** a SQL Server instance on the agent. Both use the SQL Server preinstalled on
the Microsoft-hosted Windows images (`MSSQL$SQL2019`), which is stopped by default and must be started. A
self-hosted agent, or a container, needs the equivalent — and if none is available, `CROSSBUY_SQL_REQUIRED=1` makes
that a loud failure rather than a silent 72-test skip. That is the intended behaviour.

## 5. What CI does NOT change

* It does not make the analyzer enforce anything it does not already enforce locally.
* It does not change the baseline, the manifest, or any severity.
* It adds no production dependency.

The gate is in the build. CI runs the build somewhere nobody can skip.
