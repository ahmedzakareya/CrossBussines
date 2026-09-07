@echo off
REM ============================================================================
REM  CrossBuy — build a clean production database from scratch.
REM  Edit SVR below if your instance differs. Run from this folder (deploy\sql\fresh).
REM  Order is required: create DB -> schema -> seed -> foreign keys.
REM ============================================================================
set "SVR=localhost"

echo [1/4] create database...
sqlcmd -S %SVR% -E -C -b -i "%~dp000_create_database.sql"   || goto :err
echo [2/4] schema (tables + indexes)...
sqlcmd -S %SVR% -d CrossBuyDB2 -E -C -b -i "%~dp001_schema.sql"       || goto :err
echo [3/4] seed (config + admin)...
sqlcmd -S %SVR% -d CrossBuyDB2 -E -C -b -i "%~dp003_seed_config.sql"  || goto :err
echo [4/4] foreign keys...
sqlcmd -S %SVR% -d CrossBuyDB2 -E -C -b -i "%~dp002_foreign_keys.sql" || goto :err

echo.
echo SUCCESS: CrossBuyDB2 built. Login: Admin / Admin@123
goto :eof
:err
echo.
echo FAILED on the step above (see the SQL error). Fix and re-run — scripts are idempotent.
exit /b 1
