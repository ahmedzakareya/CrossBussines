@echo off
REM ============================================================================
REM  CrossBuy - ISOLATED accounting-discovery instance
REM ----------------------------------------------------------------------------
REM  Points a PUBLISHED copy of the application at a DISPOSABLE database restored
REM  from a copy-only backup of CrossBuyDev. It does not touch the developer's
REM  running IIS Express session (port 44368) or the CrossBuyDev database.
REM
REM  Every outbound integration is switched off before the process starts:
REM    Smtp__Enabled=false            no mail leaves the machine
REM    AiService__BaseUrl=disabled    the AI proxy has nowhere to call
REM    Ai__Enabled=false              belt and braces
REM  E-invoicing needs no switch: EtaInvoiceService.SubmitSalesInvoiceAsync
REM  returns a hardcoded "NotConfigured" and makes no network call at all.
REM ============================================================================

set ASPNETCORE_ENVIRONMENT=Development
set ASPNETCORE_URLS=http://localhost:5299
set ConnectionStrings__DefaultConnection=Server=localhost;Database=CrossBuyAcctTest;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True

REM ---- outbound integrations OFF ----
set Smtp__Enabled=false
set Smtp__Host=
set Smtp__User=
set Smtp__Password=
set AiService__BaseUrl=http://127.0.0.1:1
set AiService__DestinationClass=Internal
set AiService__DeploymentMode=LocalLoopback

REM ---- identify this instance in logs and diagnostics ----
set Runtime__InstanceName=acct-discovery-isolated

cd /d C:\temp\cb_acct_iso\app
dotnet CrossBuy.dll
