-- HR8 (2026-11-09): the salary transfer file a company uploads to its bank (wage protection). Its SHAPE is a setting —
-- columns, order, headers, delimiter, date format, trailer, file name — so a bank's template is matched without code.
-- One format per company; the columns live in ColumnsJson. Idempotent.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF OBJECT_ID(N'dbo.HrBankFileFormats', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HrBankFileFormats (
        ID               int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HrBankFileFormats PRIMARY KEY,
        CompanyID        int            NOT NULL,
        BankName         nvarchar(100)  NULL,
        Delimiter        nvarchar(4)    NOT NULL CONSTRAINT DF_HrBankFileFormats_Delim DEFAULT N',',
        WithBom          bit            NOT NULL CONSTRAINT DF_HrBankFileFormats_Bom DEFAULT 1,
        HeaderRow        bit            NOT NULL CONSTRAINT DF_HrBankFileFormats_Header DEFAULT 1,
        DateFormat       nvarchar(20)   NOT NULL CONSTRAINT DF_HrBankFileFormats_Date DEFAULT N'dd/MM/yyyy',
        DecimalPlaces    int            NOT NULL CONSTRAINT DF_HrBankFileFormats_Dec DEFAULT 3,
        TrailerRow       nvarchar(20)   NOT NULL CONSTRAINT DF_HrBankFileFormats_Trailer DEFAULT N'None',   -- None | CountTotal
        FileNamePattern  nvarchar(100)  NOT NULL CONSTRAINT DF_HrBankFileFormats_File DEFAULT N'SALARIES_{yyyyMM}.csv',
        CompanyAccount   nvarchar(50)   NULL,     -- the company's debit account / IBAN, usable as a column
        EmployerCode     nvarchar(50)   NULL,     -- the employer id the bank or the authority assigns, usable as a column
        PaymentDay       int            NOT NULL CONSTRAINT DF_HrBankFileFormats_Day DEFAULT 0,          -- 0 = last day of the month
        ColumnsJson      nvarchar(max)  NOT NULL,
        UpdatedAt        datetime2      NOT NULL CONSTRAINT DF_HrBankFileFormats_At DEFAULT SYSUTCDATETIME(),
        UpdatedByEmployeeID int         NULL,
        CONSTRAINT CK_HrBankFileFormats_Dec CHECK (DecimalPlaces BETWEEN 0 AND 3),
        CONSTRAINT CK_HrBankFileFormats_Day CHECK (PaymentDay BETWEEN 0 AND 31)
    );
    CREATE UNIQUE INDEX UX_HrBankFileFormats_Company ON dbo.HrBankFileFormats (CompanyID);
END
